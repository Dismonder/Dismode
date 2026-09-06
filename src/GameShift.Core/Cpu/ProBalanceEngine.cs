using GameShift.Core.Domain.Processes;

namespace GameShift.Core.Cpu;

public enum ProBalanceAction
{
    None = 0,
    Restrain = 1,
    Release = 2,
}

public sealed record ProBalanceObservation(
    ProcessRuntimeKey RuntimeKey,
    string ProcessName,
    double CpuPercent,
    bool IsProtected,
    bool BelongsToGame);

public sealed record ProBalanceDecision(
    ProcessRuntimeKey RuntimeKey,
    string ProcessName,
    ProBalanceAction Action,
    string Reason);

/// <summary>
/// Decides, sample by sample, which background processes to hold back while a
/// game is running, and when to let them go again.
/// <para>
/// GameShift's existing priority handling is a one-off: it lowers what the user
/// approved when the session starts and restores it at the end. That does
/// nothing about a process which only starts misbehaving twenty minutes in —
/// an indexer waking up, an updater unpacking, a browser tab going haywire.
/// This is the part that watches for those.
/// </para>
/// <para>
/// The whole design problem here is not detecting a busy process, which is
/// trivial; it is not flapping. A naive threshold restrains and releases the
/// same process every other sample, and the priority churn costs more than the
/// process ever did. Hence: a process must be over the line for several samples
/// running before it is touched, it is released at a lower line than it was
/// caught at, restraint is held for a minimum time and abandoned after a
/// maximum, and there is a quiet period before the same process can be caught
/// again.
/// </para>
/// <para>
/// This type holds state but touches nothing outside itself — no processes, no
/// clock, no I/O. Time arrives as a parameter so the behaviour is testable
/// without waiting for it.
/// </para>
/// </summary>
public sealed class ProBalanceEngine
{
    private readonly ProBalanceSettings _settings;
    private readonly Dictionary<ProcessRuntimeKey, TrackedProcess> _tracked =
        [];

    public ProBalanceEngine(ProBalanceSettings? settings = null)
    {
        _settings = settings ?? new ProBalanceSettings();
    }

    /// <summary>Processes currently held back.</summary>
    public IReadOnlyCollection<ProcessRuntimeKey> Restrained =>
        [.. _tracked
            .Where(entry => entry.Value.RestrainedSinceUtc is not null)
            .Select(entry => entry.Key)];

    public IReadOnlyList<ProBalanceDecision> Evaluate(
        IReadOnlyList<ProBalanceObservation> observations,
        double systemCpuPercent,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(observations);

        List<ProBalanceDecision> decisions = [];
        HashSet<ProcessRuntimeKey> seen = [];

        foreach (ProBalanceObservation observation in observations)
        {
            seen.Add(observation.RuntimeKey);
            if (observation.IsProtected || observation.BelongsToGame)
            {
                // Powloka, anti-cheat i samo drzewo gry sa poza zasiegiem.
                // Sciszenie ktoregokolwiek z nich pogarsza dokladnie to, co
                // ta petla ma poprawiac.
                continue;
            }

            if (!_tracked.TryGetValue(
                    observation.RuntimeKey,
                    out TrackedProcess? state))
            {
                state = new();
                _tracked[observation.RuntimeKey] = state;
            }

            ProBalanceDecision? decision = Advance(
                state,
                observation,
                systemCpuPercent,
                nowUtc);
            if (decision is not null)
            {
                decisions.Add(decision);
            }
        }

        // Proces, ktory zniknal z probki, juz sie zakonczyl. Nie ma czego
        // przywracac, ale trzymanie jego stanu w nieskonczonosc to wyciek.
        foreach (ProcessRuntimeKey key in _tracked.Keys.ToArray())
        {
            if (!seen.Contains(key))
            {
                _tracked.Remove(key);
            }
        }

        return decisions;
    }

    /// <summary>
    /// Everything currently held back is released. Used when the session ends,
    /// so nothing stays throttled after the game closes.
    /// </summary>
    public IReadOnlyList<ProBalanceDecision> ReleaseAll()
    {
        List<ProBalanceDecision> decisions = [];
        foreach ((ProcessRuntimeKey key, TrackedProcess state) in _tracked)
        {
            if (state.RestrainedSinceUtc is null)
            {
                continue;
            }

            state.RestrainedSinceUtc = null;
            state.CooldownUntilUtc = null;
            decisions.Add(new(
                key,
                state.ProcessName,
                ProBalanceAction.Release,
                "Sesja zakończona."));
        }

        _tracked.Clear();
        return decisions;
    }

    private ProBalanceDecision? Advance(
        TrackedProcess state,
        ProBalanceObservation observation,
        double systemCpuPercent,
        DateTimeOffset nowUtc)
    {
        state.ProcessName = observation.ProcessName;

        if (state.RestrainedSinceUtc is DateTimeOffset restrainedSince)
        {
            return AdvanceRestrained(
                state,
                observation,
                restrainedSince,
                nowUtc);
        }

        if (state.CooldownUntilUtc is DateTimeOffset cooldownUntil)
        {
            if (nowUtc < cooldownUntil)
            {
                state.HotSamples = 0;
                return null;
            }

            state.CooldownUntilUtc = null;
        }

        if (systemCpuPercent < _settings.SystemLoadPercent)
        {
            // Maszyna nie jest obciazona, wiec nikt nikomu nie przeszkadza.
            state.HotSamples = 0;
            return null;
        }

        if (observation.CpuPercent < _settings.RestrainAbovePercent)
        {
            state.HotSamples = 0;
            return null;
        }

        state.HotSamples++;
        if (state.HotSamples < _settings.SustainedSamples)
        {
            return null;
        }

        state.HotSamples = 0;
        state.CalmSamples = 0;
        state.RestrainedSinceUtc = nowUtc;
        return new(
            observation.RuntimeKey,
            observation.ProcessName,
            ProBalanceAction.Restrain,
            $"{observation.CpuPercent:F0}% CPU przez "
                + $"{_settings.SustainedSamples} próbki przy obciążeniu "
                + $"{systemCpuPercent:F0}%.");
    }

    private ProBalanceDecision? AdvanceRestrained(
        TrackedProcess state,
        ProBalanceObservation observation,
        DateTimeOffset restrainedSince,
        DateTimeOffset nowUtc)
    {
        TimeSpan held = nowUtc - restrainedSince;
        if (held >= _settings.MaximumRestraint)
        {
            return Release(
                state,
                observation,
                nowUtc,
                "Osiągnięto maksymalny czas ograniczenia.");
        }

        if (held < _settings.MinimumRestraint)
        {
            return null;
        }

        if (observation.CpuPercent >= _settings.ReleaseBelowPercent)
        {
            state.CalmSamples = 0;
            return null;
        }

        state.CalmSamples++;
        return state.CalmSamples < _settings.CalmSamples
            ? null
            : Release(
                state,
                observation,
                nowUtc,
                $"Zużycie spadło poniżej {_settings.ReleaseBelowPercent:F0}%.");
    }

    private ProBalanceDecision Release(
        TrackedProcess state,
        ProBalanceObservation observation,
        DateTimeOffset nowUtc,
        string reason)
    {
        state.RestrainedSinceUtc = null;
        state.CalmSamples = 0;
        state.HotSamples = 0;
        state.CooldownUntilUtc = nowUtc + _settings.Cooldown;
        return new(
            observation.RuntimeKey,
            observation.ProcessName,
            ProBalanceAction.Release,
            reason);
    }

    private sealed class TrackedProcess
    {
        public string ProcessName { get; set; } = string.Empty;
        public int HotSamples { get; set; }
        public int CalmSamples { get; set; }
        public DateTimeOffset? RestrainedSinceUtc { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
    }
}

using System.Diagnostics;
using Dismode.Core.Cpu;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.Processes;

namespace Dismode.Windows.Cpu;

/// <summary>
/// Applies and undoes a restraint on one process.
/// </summary>
public interface IProBalanceActuator
{
    ValueTask<bool> RestrainAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken);

    ValueTask<bool> ReleaseAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drives <see cref="ProBalanceEngine"/> against the running machine: samples
/// processes, asks the engine what to do, and carries the answer out.
/// <para>
/// Not yet wired into a session. The engine's decisions are journaled only once
/// this runs under the orchestrator, which owns the session and the recovery
/// journal; until then the actuator undoes its own work on stop, and a crash
/// mid-session would leave a background process at a lowered priority until it
/// next restarts.
/// </para>
/// </summary>
public sealed class ProBalanceSupervisor : IAsyncDisposable
{
    private readonly ProBalanceEngine _engine;
    private readonly IProBalanceActuator _actuator;
    private readonly ICpuProcessSource _processes;
    private readonly Dictionary<ProcessRuntimeKey, CpuReading> _previous = [];
    private readonly Func<IReadOnlySet<int>> _gameProcessIds;
    private readonly TimeProvider _timeProvider;
    private readonly ProBalanceSettings _settings;

    // Jawnie podany odstep jest staly (testy, wlasny rytm wolajacego).
    // Bez niego rytm wybiera ProBalanceCadence z ustawien.
    private readonly TimeSpan? _fixedInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private double _lastBackgroundCores;
    private int _lastRestrainedCount;

    private CancellationTokenSource? _loop;
    private Task? _worker;

    public ProBalanceSupervisor(
        ICpuProcessSource processes,
        IProBalanceActuator actuator,
        Func<IReadOnlySet<int>> gameProcessIds,
        ProBalanceSettings? settings = null,
        TimeProvider? timeProvider = null,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(actuator);
        ArgumentNullException.ThrowIfNull(gameProcessIds);
        _processes = processes;
        _actuator = actuator;
        _gameProcessIds = gameProcessIds;
        _settings = settings ?? new ProBalanceSettings();
        if (interval is null
            && (!ProBalanceCadence.IsSupported(_settings.CalmInterval)
                || !ProBalanceCadence.IsSupported(_settings.BusyInterval)))
        {
            // Sprawdzone tutaj, bo w petli wyjatek z wyboru odstepu
            // zatrzymalby nadzorce na reszte sesji.
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "Odstępy próbkowania muszą mieścić się w zakresie timera.");
        }

        _engine = new(_settings);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fixedInterval = interval;
    }

    /// <summary>
    /// How often the loop samples when nobody says otherwise.
    /// <para>
    /// Was two seconds while a pass cost a handle per process. With the
    /// single-call sampler a pass measures 4,3 ms on the development machine,
    /// so one second costs 0,43% of a core and a sustained hog is caught after
    /// about three seconds instead of six. The thresholds are untouched — the
    /// same number of samples over the same line — only the wait between
    /// them shrank. Whether three seconds rather than six shows up in frame
    /// times is a measurement still owed; the loop's own cost is measured and
    /// within what the plan calls acceptable.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    public void Start()
    {
        if (_worker is not null)
        {
            return;
        }

        _loop = new();
        _worker = RunAsync(_loop.Token);
    }

    public async ValueTask StopAsync()
    {
        if (_loop is not null)
        {
            await _loop.CancelAsync().ConfigureAwait(false);
            if (_worker is not null)
            {
                try
                {
                    await _worker.ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException)
                {
                    // Worker, ktory padl na czyms nieprzewidzianym, nie moze
                    // zatrzymac zwalniania: to, co zdazyl ograniczyc, jest
                    // w silniku i w aktuatorze, a jedyne, co by sie stalo po
                    // rzuceniu stad dalej, to sesja zamknieta bez oddania
                    // procesow. Anulowanie jest tu normalnym zakonczeniem.
                }
            }

            _loop.Dispose();
            _loop = null;
            _worker = null;
        }

        // Zwolnienie nastepuje takze wtedy, gdy petla nigdy nie wystartowala:
        // nadzorca da sie prowadzic recznie przez TickAsync i wtedy tez trzyma
        // procesy, ktore trzeba oddac.
        await ReleaseEverythingAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One pass: sample, decide, act. Exposed so a test can step the loop
    /// without a timer, and so the caller can drive it from its own cadence.
    /// </summary>
    public async ValueTask<IReadOnlyList<ProBalanceDecision>> TickAsync(
        CancellationToken cancellationToken)
    {
        // Petla i wywolanie reczne dziela slownik poprzednich odczytow oraz
        // stan silnika. Bez tej bramki rownolegly przebieg uszkodzilby oba.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<ProBalanceDecision>> TickCoreAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlySet<int> gameIds = _gameProcessIds();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<ProBalanceObservation> observations = [];
        HashSet<ProcessRuntimeKey> seen = [];

        foreach (CpuProcessSample sample in _processes.Capture())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessRuntimeKey key = new(
                sample.ProcessId,
                sample.StartedAtUtc);
            seen.Add(key);
            double cpuCores = MeasureCpuCores(
                key,
                sample.TotalProcessorTime,
                now);
            observations.Add(new(
                key,
                sample.Name,
                cpuCores,
                BackgroundApplicationGuard.IsProtectedProcessName(sample.Name),
                gameIds.Contains(sample.ProcessId)));
        }

        foreach (ProcessRuntimeKey key in _previous.Keys.ToArray())
        {
            if (!seen.Contains(key))
            {
                _previous.Remove(key);
            }
        }

        // Obciazenie tla liczymy z tego, co juz zebralismy: suma rdzeni
        // procesow, ktore nie naleza do gry i nie sa chronione. Osobne
        // probkowanie calej maszyny bylo i zbedne, i mylace — wliczalo prace
        // samej gry do dowodu, ze cos grze przeszkadza.
        double backgroundCores = observations
            .Where(observation =>
                !observation.BelongsToGame && !observation.IsProtected)
            .Sum(observation => observation.CpuCores);
        _lastBackgroundCores = backgroundCores;

        IReadOnlyList<ProBalanceDecision> decisions = _engine.Evaluate(
            observations,
            backgroundCores,
            now);
        foreach (ProBalanceDecision decision in decisions)
        {
            await CarryOutAsync(decision).ConfigureAwait(false);
        }

        // Zapis pod bramka; petla czyta to po jej zwolnieniu, zeby wybrac
        // odstep do nastepnej probki.
        _lastRestrainedCount = _engine.Restrained.Count;
        return decisions;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private readonly record struct CpuReading(
        TimeSpan TotalProcessorTime,
        DateTimeOffset ObservedAtUtc);

    /// <summary>
    /// Processor time used since this process was last seen, in cores: the
    /// ratio of CPU time consumed to wall time elapsed, so 1.0 means one core
    /// kept busy throughout. Deliberately not divided by the core count —
    /// a threshold expressed as a share of the machine would catch a
    /// single-threaded hog on a small box and miss the same one on a large box.
    /// The first reading for a process has nothing to subtract, so it reports
    /// zero and the engine's "sustained for several samples" rule absorbs it.
    /// </summary>
    private double MeasureCpuCores(
        ProcessRuntimeKey key,
        TimeSpan totalProcessorTime,
        DateTimeOffset now)
    {
        double cores = 0;
        if (_previous.TryGetValue(key, out CpuReading last))
        {
            double elapsed = (now - last.ObservedAtUtc).TotalMilliseconds;
            double used =
                (totalProcessorTime - last.TotalProcessorTime)
                .TotalMilliseconds;
            if (elapsed > 0 && used >= 0)
            {
                cores = used / elapsed;
            }
        }

        _previous[key] = new(totalProcessorTime, now);
        return cores;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await TickCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException)
            {
                // Jedna nieudana probka nie moze zabic petli na cala sesje.
                // Lista wyjatkow byla tu kiedys zamknieta i Win32Exception
                // z migawki procesow przez nia przechodzil: worker padal,
                // silnik trzymal ograniczone procesy, a zatrzymanie sesji
                // dostawalo wyjatek zamiast zwolnienia.
            }
            finally
            {
                _gate.Release();
            }

            TimeSpan delay = _fixedInterval
                ?? ProBalanceCadence.NextInterval(
                    _settings,
                    _lastBackgroundCores,
                    _lastRestrainedCount);
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Carries one decision out to the end, whatever happens to the loop
    /// meanwhile.
    /// <para>
    /// The loop's cancellation token deliberately stops here. A restraint is
    /// several journaled changes followed by a record of what was done; a
    /// stop request arriving in the middle used to cancel the tail — the mask
    /// already on the process, the record of it never written — and the
    /// release that follows every stop had nothing to release. The process
    /// stayed in the background corner after the session. Stopping waits for
    /// the loop anyway, and one decision is bounded work, so finishing it is
    /// both safe and the only correct option.
    /// </para>
    /// </summary>
    private async ValueTask CarryOutAsync(ProBalanceDecision decision)
    {
        try
        {
            _ = decision.Action switch
            {
                ProBalanceAction.Restrain => await _actuator
                    .RestrainAsync(decision.RuntimeKey, CancellationToken.None)
                    .ConfigureAwait(false),
                ProBalanceAction.Release => await _actuator
                    .ReleaseAsync(decision.RuntimeKey, CancellationToken.None)
                    .ConfigureAwait(false),
                _ => false,
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or UnauthorizedAccessException)
        {
            // Proces mogl sie zakonczyc miedzy decyzja a jej wykonaniem.
        }
    }

    private async ValueTask ReleaseEverythingAsync(
        CancellationToken cancellationToken)
    {
        foreach (ProBalanceDecision decision in _engine.ReleaseAll())
        {
            await CarryOutAsync(decision).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Lowers a process to BelowNormal and puts its original class back.
/// Only ever lowers: a process already at or below BelowNormal is left alone,
/// so restraint can never accidentally promote something.
/// <para>
/// Every call re-checks that the process behind the id is still the same one.
/// Windows reuses process ids, and the gap between restraining something and
/// releasing it is long enough for the original to exit and its number to be
/// handed to something else — at which point restoring "the original priority"
/// would be writing one process's setting onto a stranger.
/// </para>
/// </summary>
public sealed class PriorityProBalanceActuator : IProBalanceActuator
{
    private readonly Dictionary<ProcessRuntimeKey, ProcessPriorityClass>
        _original = [];

    public ValueTask<bool> RestrainAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process? process = OpenMatching(runtimeKey);
            if (process is null)
            {
                return ValueTask.FromResult(false);
            }

            ProcessPriorityClass current = process.PriorityClass;
            if (current is ProcessPriorityClass.BelowNormal
                or ProcessPriorityClass.Idle)
            {
                return ValueTask.FromResult(false);
            }

            _original[runtimeKey] = current;
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            return ValueTask.FromResult(true);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<bool> ReleaseAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        if (!_original.Remove(
                runtimeKey,
                out ProcessPriorityClass original))
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            using Process? process = OpenMatching(runtimeKey);
            if (process is null)
            {
                // Oryginal zniknal. Nie ma czego przywracac, a numer moze juz
                // nalezec do kogos innego.
                return ValueTask.FromResult(false);
            }

            process.PriorityClass = original;
            return ValueTask.FromResult(true);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    /// <summary>
    /// The process behind the id, but only if its start time still matches the
    /// one the key was made from. Null when the id now belongs to something
    /// else, or to nothing.
    /// </summary>
    private static Process? OpenMatching(ProcessRuntimeKey runtimeKey)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(runtimeKey.ProcessId);
            DateTimeOffset startedAt = process.StartTime.ToUniversalTime();

            // Czasy startu roznia sie o ulamki sekundy miedzy odczytami przez
            // rozne API, wiec porownanie co do taktu odrzucaloby wlasciwy
            // proces. Sekunda wystarczy, zeby odroznic go od nastepnego
            // wlasciciela tego samego numeru.
            if (Math.Abs(
                    (startedAt - runtimeKey.StartedAtUtc).TotalSeconds) > 1)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            process?.Dispose();
            return null;
        }
    }
}

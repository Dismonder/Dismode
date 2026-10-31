using Dismode.Core.Domain.Processes;
using Dismode.Windows.Cpu;

namespace Dismode.Windows.Sessions;

public sealed class LateBackgroundRuleTracker(DateTimeOffset activatedAtUtc,
    IEnumerable<ProcessRuntimeKey>? attempted = null)
{
    private readonly HashSet<ProcessRuntimeKey> _attempted = new(attempted ?? []);
    private readonly object _sync = new();
    private HashSet<ProcessRuntimeKey> _observed = [];
    private HashSet<ProcessRuntimeKey> _next = [];

    public bool ObserveProcessSet(IReadOnlyList<CpuProcessSample> processes)
    {
        lock (_sync)
        {
            _next.Clear();
            bool added = false;
            for (int index = 0; index < processes.Count; index++)
            {
                CpuProcessSample process = processes[index];
                if (process.ProcessId <= 0) { continue; }
                ProcessRuntimeKey key = new(process.ProcessId, process.StartedAtUtc);
                _next.Add(key);
                added |= !_observed.Contains(key);
            }
            (_observed, _next) = (_next, _observed);
            return added;
        }
    }

    public DateTimeOffset ActivatedAtUtc { get; } = activatedAtUtc;

    public ProcessRuntimeKey[] SnapshotAttempted() { lock (_sync) { return [.. _attempted]; } }

    public bool Contains(ProcessRuntimeKey key) { lock (_sync) { return _attempted.Contains(key); } }

    public bool TryClaim(ProcessIdentity identity, IReadOnlyList<ProcessIdentity> gameTree,
        int foregroundProcessId)
    {
        lock (_sync)
        {
            if (identity.RuntimeKey.StartedAtUtc <= ActivatedAtUtc
                || gameTree.Any(game => game.RuntimeKey == identity.RuntimeKey)
                || !_attempted.Add(identity.RuntimeKey))
            {
                return false;
            }
            // Pierwszy plan oznacza swiadomy wybor uzytkownika. Zapamietujemy
            // probe i nie wracamy do procesu nawet po zmianie aktywnego okna.
            return identity.RuntimeKey.ProcessId != foregroundProcessId;
        }
    }
}

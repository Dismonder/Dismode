namespace Dismode.Core.Sessions;

/// <summary>
/// Game instances the automatic optimization must leave alone: the instance
/// whose session the user ended by hand, and the instance whose automatic
/// start failed. Keyed by process id together with the process start time,
/// because Windows hands a finished process's id to a new process; an entry
/// whose start time no longer matches is dead, and a fresh launch of the
/// same game qualifies again.
/// </summary>
public sealed class AutomaticOptimizationSkipList
{
    private readonly Dictionary<int, DateTime?> _entries = [];

    public int Count => _entries.Count;

    public void Skip(int processId, DateTime? startedAtUtc)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "A process id must be positive.");
        }

        _entries[processId] = startedAtUtc?.ToUniversalTime();
    }

    /// <summary>
    /// True when the instance is on the list. A start time unknown on either
    /// side cannot tell a reused id from the original process, so the
    /// instance stays skipped: skipping errs towards leaving a game alone.
    /// A known start time that differs means the id was reused; the stale
    /// entry is dropped and the new process qualifies.
    /// </summary>
    public bool IsSkipped(int processId, DateTime? startedAtUtc)
    {
        if (!_entries.TryGetValue(processId, out DateTime? skippedStart))
        {
            return false;
        }

        if (skippedStart is null
            || startedAtUtc is null
            || skippedStart.Value == startedAtUtc.Value.ToUniversalTime())
        {
            return true;
        }

        _ = _entries.Remove(processId);
        return false;
    }

    /// <summary>
    /// Drops the entries of processes that no longer exist. Call it only
    /// with a complete enumeration: an empty or partial set would forget
    /// instances that are still running.
    /// </summary>
    /// <returns>How many entries were dropped.</returns>
    public int ForgetExited(IReadOnlySet<int> liveProcessIds)
    {
        ArgumentNullException.ThrowIfNull(liveProcessIds);
        if (_entries.Count == 0)
        {
            return 0;
        }

        List<int> exited = _entries.Keys
            .Where(processId => !liveProcessIds.Contains(processId))
            .ToList();
        foreach (int processId in exited)
        {
            _ = _entries.Remove(processId);
        }

        return exited.Count;
    }
}

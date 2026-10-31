namespace Dismode.Core.History;

/// <summary>
/// The four numbers above the results page: how many sessions, how long they
/// ran, how many changes Dismode applied and how many sessions ended with a
/// conflict or an error. Computed for all games or for a single one.
/// </summary>
public sealed record SessionHistoryTotals(
    int SessionCount,
    TimeSpan TotalDuration,
    int AppliedActionCount,
    int TroubledSessionCount)
{
    public static SessionHistoryTotals Compute(
        IEnumerable<SessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        int count = 0;
        TimeSpan duration = TimeSpan.Zero;
        int appliedActions = 0;
        int troubled = 0;
        foreach (SessionSummary session in sessions)
        {
            count++;
            duration += session.Duration;
            appliedActions += session.AppliedActionCount;
            if (session.ErrorCount > 0 || session.ConflictCount > 0)
            {
                troubled++;
            }
        }

        return new(count, duration, appliedActions, troubled);
    }
}

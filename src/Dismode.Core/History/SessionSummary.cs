using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.History;

public sealed record SessionSummary
{
    public SessionSummary(
        SessionId sessionId,
        GameProfileId profileId,
        string gameDisplayName,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        SessionCompletionStatus status,
        int appliedActionCount,
        int restoredActionCount,
        int conflictCount,
        int errorCount,
        SessionFrameRateStatistics? frameRateStatistics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDisplayName);

        DateTimeOffset started = startedAtUtc.ToUniversalTime();
        DateTimeOffset ended = endedAtUtc.ToUniversalTime();
        if (ended < started)
        {
            throw new ArgumentException(
                "A session cannot end before it starts.",
                nameof(endedAtUtc));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "The session status is not recognized.");
        }

        if (appliedActionCount < 0
            || restoredActionCount < 0
            || restoredActionCount > appliedActionCount
            || conflictCount < 0
            || errorCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedActionCount),
                "Session counters must be non-negative and internally consistent.");
        }

        SessionId = sessionId;
        ProfileId = profileId;
        GameDisplayName = gameDisplayName.Trim();
        StartedAtUtc = started;
        EndedAtUtc = ended;
        Status = status;
        AppliedActionCount = appliedActionCount;
        RestoredActionCount = restoredActionCount;
        ConflictCount = conflictCount;
        ErrorCount = errorCount;
        FrameRateStatistics = frameRateStatistics;
    }

    public SessionId SessionId { get; }

    public GameProfileId ProfileId { get; }

    public string GameDisplayName { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset EndedAtUtc { get; }

    public SessionCompletionStatus Status { get; }

    public int AppliedActionCount { get; }

    public int RestoredActionCount { get; }

    public int ConflictCount { get; }

    public int ErrorCount { get; }

    public SessionFrameRateStatistics? FrameRateStatistics { get; }
}

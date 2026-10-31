using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.History;

/// <summary>
/// The sessions of one game, newest first, so the results page can give every
/// game its own place instead of one mixed log.
/// </summary>
public sealed record GameSessionHistory
{
    private GameSessionHistory(
        GameProfileId profileId,
        IReadOnlyList<SessionSummary> sessions)
    {
        ProfileId = profileId;
        Sessions = sessions;
        LatestSession = sessions[0];
        GameDisplayName = LatestSession.GameDisplayName;
        LatestFrameRate = sessions
            .Select(session => session.FrameRateStatistics)
            .FirstOrDefault(statistics => statistics is not null);
        Totals = SessionHistoryTotals.Compute(sessions);
    }

    public GameProfileId ProfileId { get; }

    /// <summary>The name the game had in its most recent session.</summary>
    public string GameDisplayName { get; }

    /// <summary>Sessions of this game, most recently ended first.</summary>
    public IReadOnlyList<SessionSummary> Sessions { get; }

    public SessionSummary LatestSession { get; }

    /// <summary>
    /// Frame-rate statistics of the newest session that has a measurement;
    /// a later session without enough samples does not hide it.
    /// </summary>
    public SessionFrameRateStatistics? LatestFrameRate { get; }

    public SessionHistoryTotals Totals { get; }

    /// <summary>
    /// Groups sessions by game profile. Games are ordered by their most
    /// recently ended session; ties keep the order of the input.
    /// </summary>
    public static IReadOnlyList<GameSessionHistory> GroupByGame(
        IEnumerable<SessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return sessions
            .OrderByDescending(session => session.EndedAtUtc)
            .GroupBy(session => session.ProfileId)
            .Select(group => new GameSessionHistory(group.Key, [.. group]))
            .ToArray();
    }
}

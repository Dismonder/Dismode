using Dismode.Core.Domain.Identifiers;
using Dismode.Core.History;

namespace Dismode.UnitTests;

[TestClass]
public sealed class GameSessionHistoryTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly GameProfileId Roblox = GameProfileId.Create();

    private static readonly GameProfileId Cyberpunk = GameProfileId.Create();

    [TestMethod]
    public void EmptyHistoryHasNoGamesAndZeroTotals()
    {
        Assert.IsEmpty(GameSessionHistory.GroupByGame([]));

        SessionHistoryTotals totals = SessionHistoryTotals.Compute([]);
        Assert.AreEqual(0, totals.SessionCount);
        Assert.AreEqual(TimeSpan.Zero, totals.TotalDuration);
        Assert.AreEqual(0, totals.AppliedActionCount);
        Assert.AreEqual(0, totals.TroubledSessionCount);
    }

    [TestMethod]
    public void SessionsAreGroupedPerGameWithTheMostRecentlyPlayedFirst()
    {
        SessionSummary robloxOld = Session(Roblox, "Roblox", endedHoursAgo: 30);
        SessionSummary cyberpunk = Session(Cyberpunk, "Cyberpunk 2077", endedHoursAgo: 10);
        SessionSummary robloxNew = Session(Roblox, "Roblox", endedHoursAgo: 2);

        IReadOnlyList<GameSessionHistory> games =
            GameSessionHistory.GroupByGame([cyberpunk, robloxOld, robloxNew]);

        Assert.HasCount(2, games);
        Assert.AreEqual(Roblox, games[0].ProfileId);
        CollectionAssert.AreEqual(
            new[] { robloxNew, robloxOld },
            games[0].Sessions.ToArray());
        Assert.AreSame(robloxNew, games[0].LatestSession);
        Assert.AreEqual(Cyberpunk, games[1].ProfileId);
        CollectionAssert.AreEqual(new[] { cyberpunk }, games[1].Sessions.ToArray());
    }

    [TestMethod]
    public void GameNameComesFromItsLatestSession()
    {
        IReadOnlyList<GameSessionHistory> games = GameSessionHistory.GroupByGame(
        [
            Session(Roblox, "Roblox Player", endedHoursAgo: 20),
            Session(Roblox, "Roblox", endedHoursAgo: 1),
        ]);

        Assert.AreEqual("Roblox", games.Single().GameDisplayName);
    }

    [TestMethod]
    public void LatestFrameRateSkipsNewerSessionsWithoutMeasurement()
    {
        SessionFrameRateStatistics measured = new(2323, 59.1, 16.92, 9.6, 104.31, 4.1, 1.9, 8);
        IReadOnlyList<GameSessionHistory> games = GameSessionHistory.GroupByGame(
        [
            Session(Roblox, "Roblox", endedHoursAgo: 1),
            Session(Roblox, "Roblox", endedHoursAgo: 5, frameRate: measured),
            Session(Roblox, "Roblox", endedHoursAgo: 9, frameRate: new(10, 30, 33.3, 20, 50)),
        ]);

        Assert.AreSame(measured, games.Single().LatestFrameRate);
    }

    [TestMethod]
    public void GameWithoutAnyMeasurementHasNoLatestFrameRate()
    {
        GameSessionHistory game = GameSessionHistory.GroupByGame(
            [Session(Cyberpunk, "Cyberpunk 2077", endedHoursAgo: 1)]).Single();

        Assert.IsNull(game.LatestFrameRate);
    }

    [TestMethod]
    public void TotalsCountPlaytimeActionsAndTroubledSessionsOfTheGivenSessions()
    {
        SessionHistoryTotals totals = SessionHistoryTotals.Compute(
        [
            Session(Roblox, "Roblox", endedHoursAgo: 1, minutes: 5, applied: 3),
            Session(Roblox, "Roblox", endedHoursAgo: 2, minutes: 4, conflicts: 1),
            Session(Roblox, "Roblox", endedHoursAgo: 3, minutes: 1, errors: 2),
        ]);

        Assert.AreEqual(3, totals.SessionCount);
        Assert.AreEqual(TimeSpan.FromMinutes(10), totals.TotalDuration);
        Assert.AreEqual(3, totals.AppliedActionCount);
        Assert.AreEqual(2, totals.TroubledSessionCount);
    }

    [TestMethod]
    public void EachGameCarriesTotalsOfOnlyItsOwnSessions()
    {
        IReadOnlyList<GameSessionHistory> games = GameSessionHistory.GroupByGame(
        [
            Session(Roblox, "Roblox", endedHoursAgo: 1, minutes: 5, errors: 1),
            Session(Cyberpunk, "Cyberpunk 2077", endedHoursAgo: 2, minutes: 4),
            Session(Roblox, "Roblox", endedHoursAgo: 3, minutes: 6),
        ]);

        SessionHistoryTotals roblox = games.Single(game => game.ProfileId == Roblox).Totals;
        Assert.AreEqual(2, roblox.SessionCount);
        Assert.AreEqual(TimeSpan.FromMinutes(11), roblox.TotalDuration);
        Assert.AreEqual(1, roblox.TroubledSessionCount);
        Assert.AreEqual(
            0,
            games.Single(game => game.ProfileId == Cyberpunk).Totals.TroubledSessionCount);
    }

    private static SessionSummary Session(
        GameProfileId profileId,
        string name,
        int endedHoursAgo,
        int minutes = 3,
        int applied = 0,
        int conflicts = 0,
        int errors = 0,
        SessionFrameRateStatistics? frameRate = null)
    {
        DateTimeOffset ended = Origin.AddHours(-endedHoursAgo);
        return new(
            SessionId.Create(),
            profileId,
            name,
            ended.AddMinutes(-minutes),
            ended,
            SessionCompletionStatus.Completed,
            applied,
            0,
            conflicts,
            errors,
            frameRate);
    }
}

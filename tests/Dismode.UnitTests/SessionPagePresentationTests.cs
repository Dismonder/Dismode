using Dismode.Core.Domain.Identifiers;
using Dismode.Core.History;
using Dismode.Core.Profiles;
using Dismode.UI.Services;

namespace Dismode.UnitTests;

[TestClass]
public sealed class SessionPagePresentationTests
{
    [TestMethod]
    public void BackgroundSnapshotCountsEveryCandidateAndUsesUnmodifiedReadings()
    {
        (int count, long bytes) = SessionPagePresentation.BackgroundSnapshot([100, 200, 300]);

        Assert.AreEqual(3, count);
        Assert.AreEqual(600L, bytes);
        Assert.AreEqual((0, 0L), SessionPagePresentation.BackgroundSnapshot([]));
    }

    [TestMethod]
    public void PlayTimeUsesRecordedGameplayAndSkipsUnknownDurations()
    {
        SessionSummary[] history = [Summary(20), Summary(null), Summary(40)];

        Assert.AreEqual(TimeSpan.FromSeconds(60), SessionPagePresentation.RecordedPlayTime(history));
        Assert.IsNull(SessionPagePresentation.RecordedPlayTime([Summary(null)]));
        Assert.IsNull(SessionPagePresentation.RecordedPlayTime([]));
        Assert.AreEqual(TimeSpan.Zero, SessionPagePresentation.RecordedPlayTime([Summary(0)]));
    }

    [TestMethod]
    public void SummaryOnlyNamesEnabledActionsAndKeepsBackgroundSelectionExplicit()
    {
        string none = SessionPagePresentation.DescribeOptions(false, false, false, false, false, false, false, false, false, 0);
        Assert.AreEqual("Uruchomi grę bez dodatkowych zmian.", none);

        string selected = SessionPagePresentation.DescribeOptions(true, false, true, false, false, true, true, false, false, 2);
        StringAssert.Contains(selected, "zapamięta ustawienia");
        StringAssert.Contains(selected, "wybierze wydajne rdzenie");
        StringAssert.Contains(selected, "ustawi najwyższe odświeżanie");
        StringAssert.Contains(selected, "zmierzy FPS");
        StringAssert.Contains(selected, "zastosuje wybrane działania w tle (2)");
        Assert.IsFalse(selected.Contains("pokaże nakładkę", StringComparison.Ordinal));
        Assert.IsFalse(selected.Contains("nie pozwoli ekranowi zasnąć", StringComparison.Ordinal));
        StringAssert.Contains(SessionPagePresentation.DescribeOptions(false, true, false, true, true, false, true, true, true, 0), "automatycznie dołączy do gry");
    }

    [TestMethod]
    public void FourthOverlayStyleKeepsPersistedValuesAndTheDefault()
    {
        Assert.AreEqual(1, (int)Enum.Parse<PerformanceOverlayStyle>("FullDeck"));
        Assert.AreEqual(2, (int)Enum.Parse<PerformanceOverlayStyle>("CompactBar"));
        Assert.AreEqual(3, (int)Enum.Parse<PerformanceOverlayStyle>("MinimalText"));
        Assert.AreEqual(4, (int)Enum.Parse<PerformanceOverlayStyle>("FpsAndLows"));
        PerformanceOverlayPreferences preferences = new(true, 94, 100, PerformanceOverlayCorner.TopRight, DateTimeOffset.UtcNow);
        Assert.AreEqual(PerformanceOverlayStyle.FullDeck, preferences.Style);
    }

    private static SessionSummary Summary(long? seconds) => new(
        new SessionId(Guid.NewGuid()), new GameProfileId(Guid.NewGuid()), "Gra",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1),
        SessionCompletionStatus.Completed, 0, 0, 0, 0, durationSeconds: seconds);
}

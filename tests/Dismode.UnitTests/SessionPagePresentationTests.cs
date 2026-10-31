using Dismode.Core.Domain.Identifiers;
using Dismode.Core.History;
using Dismode.Core.Profiles;
using Dismode.UI.Services;

namespace Dismode.UnitTests;

[TestClass]
public sealed class SessionPagePresentationTests
{
    private static readonly string[] SelectedOptionLabels =
        ["Pamiętaj mój wybór", "Mocne rdzenie", "Pełne odświeżanie", "Mierz FPS", "Aplikacje w tle"];

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
    public void SummaryOnlyNamesEnabledOptionsInTileOrder()
    {
        Assert.IsEmpty(SessionPagePresentation.OptionLabels(false, false, false, false, false, false, false, false, false, 0));

        CollectionAssert.AreEqual(
            SelectedOptionLabels,
            SessionPagePresentation.OptionLabels(true, false, true, false, false, true, true, false, false, 2).ToArray());
    }

    [TestMethod]
    [DataRow(0, "Pamiętaj mój wybór")]
    [DataRow(1, "Start automatyczny")]
    [DataRow(2, "Mocne rdzenie")]
    [DataRow(3, "Ekran nie zasypia")]
    [DataRow(4, "Pilnuj tła")]
    [DataRow(5, "Pełne odświeżanie")]
    [DataRow(6, "Mierz FPS")]
    [DataRow(7, "Licznik w grze")]
    [DataRow(8, "Ograniczaj procesy tła w trakcie gry")]
    [DataRow(9, "Aplikacje w tle")]
    public void EachOptionIndependentlyControlsItsChip(int option, string label)
    {
        bool[] enabled = new bool[10];
        enabled[option] = true;

        CollectionAssert.AreEqual(new[] { label }, SessionPagePresentation.OptionLabels(
            enabled[0], enabled[1], enabled[2], enabled[3], enabled[4], enabled[5],
            enabled[6], enabled[7], enabled[8], enabled[9] ? 1 : 0).ToArray());
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

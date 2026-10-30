using System.Reflection;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class PresentMonDisplayedFramesTests
{
    private const string HeaderWithDisplay =
        "Application,ProcessID,SwapChainAddress,FrameTime,DisplayedTime";

    private const string HeaderWithoutDisplay =
        "Application,ProcessID,SwapChainAddress,FrameTime";

    private static readonly string[] ArgumentsWithDisplayTrackingOff =
    [
        "--process_id", "42", "--output_stdout", "--no_console_stats",
        "--v2_metrics", "--no_track_display", "--no_track_gpu",
        "--no_track_input", "--set_circular_buffer_size", "8192",
        "--terminate_on_proc_exit", "--stop_existing_session",
        "--session_name", "Session",
    ];

    [TestMethod]
    public void DisplayTrackingOffKeepsTheCaptureArgumentsUnchanged()
    {
        IReadOnlyList<string> arguments =
            PresentMonFrameRateProvider.BuildCaptureArguments(
                [42],
                trackGpu: false,
                trackDisplay: false,
                "Session");

        CollectionAssert.AreEqual(
            ArgumentsWithDisplayTrackingOff,
            arguments.ToArray());
    }

    [TestMethod]
    public void DisplayTrackingOnOnlyDropsTheNoTrackDisplayFlag()
    {
        IReadOnlyList<string> arguments =
            PresentMonFrameRateProvider.BuildCaptureArguments(
                [42],
                trackGpu: false,
                trackDisplay: true,
                "Session");

        CollectionAssert.AreEqual(
            ArgumentsWithDisplayTrackingOff
                .Where(argument => argument != "--no_track_display")
                .ToArray(),
            arguments.ToArray());
    }

    [TestMethod]
    public void ParserWithDisplayTrackingOffIgnoresTheDisplayedColumn()
    {
        PresentMonCsvParser parser = new();
        _ = parser.TryParse(HeaderWithDisplay, out _);

        bool parsed = parser.TryParse(
            "Game.exe,42,0xA,10.0,NA",
            out PresentMonFrame frame);

        Assert.IsTrue(parsed);
        Assert.IsFalse(frame.DisplayTracked);
        Assert.IsNull(frame.DisplayedTimeMilliseconds);
        Assert.IsFalse(frame.IsDropped);
    }

    [TestMethod]
    public void ParserReadsDisplayedTimeAndTreatsMissingValuesAsDropped()
    {
        PresentMonCsvParser parser = new(trackDisplay: true);
        _ = parser.TryParse(HeaderWithDisplay, out _);

        Assert.IsTrue(parser.TryParse("Game.exe,42,0xA,10.0,16.6", out PresentMonFrame shown));
        Assert.IsTrue(parser.TryParse("Game.exe,42,0xA,10.0,NA", out PresentMonFrame notAvailable));
        Assert.IsTrue(parser.TryParse("Game.exe,42,0xA,10.0,", out PresentMonFrame empty));
        Assert.IsTrue(parser.TryParse("Game.exe,42,0xA,10.0,0", out PresentMonFrame zero));

        Assert.IsTrue(shown.DisplayTracked);
        Assert.AreEqual(16.6d, shown.DisplayedTimeMilliseconds);
        Assert.IsFalse(shown.IsDropped);
        Assert.IsTrue(notAvailable.IsDropped);
        Assert.IsTrue(empty.IsDropped);
        Assert.IsTrue(zero.IsDropped);
    }

    [TestMethod]
    public void MissingDisplayedColumnFallsBackToPresentOnlyFrames()
    {
        PresentMonCsvParser parser = new(trackDisplay: true);
        _ = parser.TryParse(HeaderWithoutDisplay, out _);

        bool parsed = parser.TryParse(
            "Game.exe,42,0xA,10.0",
            out PresentMonFrame frame);

        Assert.IsTrue(parsed);
        Assert.IsFalse(frame.DisplayTracked);
        Assert.IsFalse(frame.IsDropped);
        Assert.AreEqual(10d, frame.FrameTimeMilliseconds);
    }

    [TestMethod]
    public async Task AllFramesDisplayedGivesPresentRateAndNoDrops()
    {
        string[] rows = Enumerable.Repeat("Game.exe,42,0xA,10.0,10.0", 100).ToArray();

        (FrameRateSample sample, SessionFrameRateStatisticsAccumulator statistics) =
            await RunAsync(HeaderWithDisplay, rows, trackDisplay: true);

        Assert.AreEqual(100d, sample.FramesPerSecond!.Value, 0.01d);
        Assert.AreEqual(100d, sample.DisplayedFramesPerSecond!.Value, 0.01d);
        Assert.AreEqual(0, sample.DroppedFrames);
        Assert.AreEqual(0, statistics.SnapshotDisplayed()!.DroppedFrames);
        Assert.AreEqual(100d, statistics.Snapshot()!.OnePercentLowFramesPerSecond!.Value, 0.5d);
    }

    [TestMethod]
    public async Task SomeDroppedFramesLowerDisplayedRateAndLowsUseDisplayedFramesOnly()
    {
        // 8 wyswietlonych, 2 zgubione; po zgubieniu poprzednia klatka zostaje
        // na ekranie dluzej (20 ms).
        List<string> rows = [];
        for (int index = 0; index < 6; index++)
        {
            rows.Add("Game.exe,42,0xA,10.0,10.0");
        }

        rows.Add("Game.exe,42,0xA,10.0,20.0");
        rows.Add("Game.exe,42,0xA,10.0,NA");
        rows.Add("Game.exe,42,0xA,10.0,20.0");
        rows.Add("Game.exe,42,0xA,10.0,NA");

        (FrameRateSample sample, SessionFrameRateStatisticsAccumulator statistics) =
            await RunAsync(HeaderWithDisplay, rows, trackDisplay: true);

        Assert.AreEqual(100d, sample.FramesPerSecond!.Value, 0.01d);
        // 6 x 10 ms + 2 x 20 ms = 100 ms na 8 klatek => 80 FPS.
        Assert.AreEqual(80d, sample.DisplayedFramesPerSecond!.Value, 0.01d);
        Assert.AreEqual(2, sample.DroppedFrames);
        Assert.AreEqual(2, statistics.SnapshotDisplayed()!.DroppedFrames);
        Assert.AreEqual(8, statistics.SnapshotDisplayed()!.DisplayedFrameCount);
        // Low z klatek prezentacji dalby 100 FPS; z wyswietlonych 50 FPS.
        Assert.AreEqual(50d, statistics.Snapshot()!.OnePercentLowFramesPerSecond!.Value, 0.5d);
        Assert.AreEqual(50d, statistics.Snapshot()!.PointOnePercentLowFramesPerSecond!.Value, 0.5d);
        Assert.AreEqual(10, statistics.Snapshot()!.SampleCount);
    }

    [TestMethod]
    public async Task RunOfDropsAtTheEndIsCountedAndDoesNotInflateDisplayedRate()
    {
        List<string> rows = [];
        for (int index = 0; index < 20; index++)
        {
            rows.Add("Game.exe,42,0xA,10.0,10.0");
        }

        for (int index = 0; index < 5; index++)
        {
            rows.Add("Game.exe,42,0xA,10.0,NA");
        }

        (FrameRateSample sample, SessionFrameRateStatisticsAccumulator statistics) =
            await RunAsync(HeaderWithDisplay, rows, trackDisplay: true);

        Assert.AreEqual(5, sample.DroppedFrames);
        Assert.AreEqual(100d, sample.DisplayedFramesPerSecond!.Value, 0.01d);
        Assert.AreEqual(5, statistics.SnapshotDisplayed()!.DroppedFrames);
        Assert.AreEqual(20, statistics.SnapshotDisplayed()!.DisplayedFrameCount);
        Assert.AreEqual(100d, statistics.Snapshot()!.OnePercentLowFramesPerSecond!.Value, 0.5d);
    }

    [TestMethod]
    public async Task MissingColumnWithTrackingOnFallsBackToPresentValuesWithoutError()
    {
        string[] rows = Enumerable.Repeat("Game.exe,42,0xA,10.0", 50).ToArray();

        (FrameRateSample sample, SessionFrameRateStatisticsAccumulator statistics) =
            await RunAsync(HeaderWithoutDisplay, rows, trackDisplay: true);

        Assert.AreEqual(FrameRateStatus.Measuring, sample.Status);
        Assert.AreEqual(100d, sample.FramesPerSecond!.Value, 0.01d);
        Assert.IsNull(sample.DisplayedFramesPerSecond);
        Assert.IsNull(sample.DroppedFrames);
        Assert.IsNull(statistics.SnapshotDisplayed());
        Assert.AreEqual(100d, statistics.Snapshot()!.OnePercentLowFramesPerSecond!.Value, 0.5d);
    }

    [TestMethod]
    public async Task TrackingOffKeepsSampleAndStatisticsExactlyAsBefore()
    {
        string[] rows =
        [
            "Game.exe,42,0xA,10.0,10.0",
            "Game.exe,42,0xA,10.0,NA",
            "Game.exe,42,0xA,10.0,20.0",
        ];

        (FrameRateSample sample, SessionFrameRateStatisticsAccumulator statistics) =
            await RunAsync(HeaderWithDisplay, rows, trackDisplay: false);

        Assert.IsNull(sample.DisplayedFramesPerSecond);
        Assert.IsNull(sample.DroppedFrames);
        Assert.IsNull(statistics.SnapshotDisplayed());
        Assert.AreEqual(3, statistics.Snapshot()!.SampleCount);
        Assert.AreEqual(100d, sample.FramesPerSecond!.Value, 0.01d);
    }

    [TestMethod]
    [DataRow(7, 0, 100d, DisplayName = "7 wyswietlonych: ponizej progu, low z prezentacji")]
    [DataRow(8, 0, 50d, DisplayName = "8 wyswietlonych: prog osiagniety, low z wyswietlonych")]
    [DataRow(10, 12, 100d, DisplayName = "10 z 22: ponizej polowy, low z prezentacji")]
    [DataRow(11, 11, 50d, DisplayName = "11 z 22: dokladnie polowa, low z wyswietlonych")]
    public void DisplayedLowsNeedAMeaningfulSample(
        int displayedFrames,
        int droppedFrames,
        double expectedLow)
    {
        SessionFrameRateStatisticsAccumulator statistics = new();
        for (int index = 0; index < displayedFrames; index++)
        {
            statistics.Add(10d, displayedTimeMilliseconds: 20d);
        }

        for (int index = 0; index < droppedFrames; index++)
        {
            statistics.Add(10d, dropped: true);
        }

        Assert.AreEqual(
            expectedLow,
            statistics.Snapshot()!.OnePercentLowFramesPerSecond!.Value,
            0.5d);
        Assert.AreEqual(
            expectedLow,
            statistics.Snapshot()!.PointOnePercentLowFramesPerSecond!.Value,
            0.5d);
        Assert.AreEqual(displayedFrames, statistics.SnapshotDisplayed()!.DisplayedFrameCount);
    }

    private static async Task<(FrameRateSample Sample, SessionFrameRateStatisticsAccumulator Statistics)>
        RunAsync(string header, IEnumerable<string> rows, bool trackDisplay)
    {
        await using PresentMonFrameRateProvider provider = new();
        SessionFrameRateStatisticsAccumulator statistics = new();
        provider.SetSessionStatistics(statistics);
        MethodInfo add = typeof(PresentMonFrameRateProvider).GetMethod(
            "AddObservation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo sample = typeof(PresentMonFrameRateProvider).GetMethod(
            "CreateCurrentSampleCore", BindingFlags.Instance | BindingFlags.NonPublic)!;

        PresentMonCsvParser parser = new(trackDisplay);
        Assert.IsFalse(parser.TryParse(header, out _));
        foreach (string row in rows)
        {
            Assert.IsTrue(parser.TryParse(row, out PresentMonFrame frame));
            _ = add.Invoke(provider, [frame]);
        }

        FrameRateSample result = (FrameRateSample)sample.Invoke(provider, [null])!;
        return (result, statistics);
    }
}

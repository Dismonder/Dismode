using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class PresentMonFrameRateProviderTests
{
    [TestMethod]
    public void VersionTwoCsvReturnsRealPidSwapChainAndFrameTime()
    {
        PresentMonCsvParser parser = new();

        bool headerWasFrame = parser.TryParse(
            "Application,ProcessID,SwapChainAddress,FrameTime",
            out _);
        bool rowWasFrame = parser.TryParse(
            "\"Game, Test.exe\",23456,0xAABBCCDD,16.6667",
            out PresentMonFrame frame);

        Assert.IsFalse(headerWasFrame);
        Assert.IsTrue(parser.HasValidHeader);
        Assert.IsTrue(rowWasFrame);
        Assert.AreEqual(23_456, frame.ProcessId);
        Assert.AreEqual("0xAABBCCDD", frame.SwapChainAddress);
        Assert.AreEqual(
            16.6667d,
            frame.FrameTimeMilliseconds,
            0.0001d);
    }

    [TestMethod]
    public void MissingOrNonNumericFrameTimeIsNotReportedAsFps()
    {
        PresentMonCsvParser parser = new();
        _ = parser.TryParse(
            "Application,ProcessID,SwapChainAddress,FrameTime",
            out _);

        bool missing = parser.TryParse(
            "Game.exe,23456,0xAABBCCDD,NA",
            out _);
        bool zero = parser.TryParse(
            "Game.exe,23456,0xAABBCCDD,0",
            out _);

        Assert.IsFalse(missing);
        Assert.IsFalse(zero);
    }

    [TestMethod]
    public void VersionTwoCsvReturnsOptionalGpuBusyTime()
    {
        PresentMonCsvParser parser = new();
        _ = parser.TryParse(
            "Application,ProcessID,SwapChainAddress,FrameTime,GPUBusy",
            out _);

        bool parsed = parser.TryParse(
            "Game.exe,23456,0xAABBCCDD,16.0000,8.0000",
            out PresentMonFrame frame);

        Assert.IsTrue(parsed);
        Assert.AreEqual(8d, frame.GpuBusyMilliseconds);
    }

    [TestMethod]
    public async Task CaptureTargetPrefersTheVerifiedProcessWithAGameWindow()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();

        int? selected =
            PresentMonFrameRateProvider.SelectCaptureTargetProcessId(
                [Environment.ProcessId, harness.Process.Id]);

        Assert.AreEqual(harness.Process.Id, selected);
    }

    [TestMethod]
    public void CaptureSessionNameIsStablePerUserAndDoesNotContainSid()
    {
        const string firstSid =
            "S-1-5-21-111111111-222222222-333333333-1001";
        const string secondSid =
            "S-1-5-21-111111111-222222222-333333333-1002";

        string first =
            PresentMonFrameRateProvider.CreateCaptureSessionName(firstSid);
        string repeated =
            PresentMonFrameRateProvider.CreateCaptureSessionName(firstSid);
        string second =
            PresentMonFrameRateProvider.CreateCaptureSessionName(secondSid);

        Assert.AreEqual(first, repeated);
        Assert.AreNotEqual(first, second);
        StringAssert.StartsWith(first, "GameShift-");
        Assert.IsFalse(
            first.Contains(firstSid, StringComparison.Ordinal));
        Assert.AreEqual("GameShift-".Length + 16, first.Length);
    }

    [TestMethod]
    public void BenchmarkCollectorKeepsRawFramesFromDominantSwapChain()
    {
        BenchmarkFrameCollector collector = new(targetProcessId: 42);
        collector.Add(new(42, "0xSECONDARY", 30));
        collector.Add(new(7, "0xMAIN", 99));
        collector.Add(new(42, "0xMAIN", 16));
        collector.Add(new(42, "0xMAIN", 17));
        collector.Add(new(42, "0xMAIN", 18));

        IReadOnlyList<double> frames = collector.SnapshotDominantStream();

        double[] expected = [16d, 17d, 18d];
        CollectionAssert.AreEqual(expected, frames.ToArray());
    }

    [TestMethod]
    public void BenchmarkCollectorCalculatesGpuBusyForDominantSwapChain()
    {
        BenchmarkFrameCollector collector = new(targetProcessId: 42);
        collector.Add(new(42, "0xSECONDARY", 30, 30));
        collector.Add(new(42, "0xMAIN", 16, 8));
        collector.Add(new(42, "0xMAIN", 20, 10));

        PresentMonBenchmarkCapture capture =
            collector.SnapshotDominantCapture();

        double[] expected = [16d, 20d];
        CollectionAssert.AreEqual(
            expected,
            capture.FrameTimesMilliseconds.ToArray());
        Assert.AreEqual(50d, capture.AverageGpuBusyPercent);
    }

    [TestMethod]
    public async Task MissingPinnedExecutableIsReportedWithoutLaunchingAnything()
    {
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            "GameShift.PresentMonTests",
            Guid.NewGuid().ToString("N"),
            PresentMonComponent.ExecutableFileName);

        PresentMonComponentInspection inspection =
            await PresentMonComponent.InspectAsync(
                missingPath,
                CancellationToken.None);

        Assert.AreEqual(
            PresentMonComponentState.Missing,
            inspection.State);
        Assert.IsFalse(inspection.IsReady);
        Assert.IsFalse(File.Exists(missingPath));
    }

    [TestMethod]
    public async Task StopTerminatesOwnedCaptureAndClearsTrackedHandle()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        string executablePath = FindPinnedPresentMonExecutable();
        await using PresentMonFrameRateProvider provider = new(
            executablePath,
            $"integration-test-{Guid.NewGuid():N}");

        FrameRateSample starting = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);
        int captureProcessId = provider.ActiveCaptureProcessId
            ?? throw new AssertFailedException(
                "Provider nie zapisał własnego procesu capture: "
                + $"{starting.Status}; {starting.Message}");

        Assert.AreEqual(FrameRateStatus.Starting, starting.Status);
        await provider.StopAsync(CancellationToken.None);

        Assert.IsNull(provider.ActiveCaptureProcessId);
        Assert.IsFalse(IsProcessRunning(captureProcessId));
    }

    private static string FindPinnedPresentMonExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "The GameShift solution root could not be located.");
        }

        string path = Path.Combine(
            directory.FullName,
            "third_party",
            "PresentMon",
            PresentMonComponent.ExecutableFileName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The pinned PresentMon test component is missing.",
                path);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

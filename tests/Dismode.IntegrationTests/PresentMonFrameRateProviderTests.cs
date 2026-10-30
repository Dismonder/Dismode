using System.Reflection;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class PresentMonFrameRateProviderTests
{
    [TestMethod]
    public async Task SessionFrameRateReceivesEveryFrameOfSelectedStreamWithoutPollingDuplicates()
    {
        await using PresentMonFrameRateProvider provider = new();
        SessionFrameRateStatisticsAccumulator statistics = new();
        provider.SetSessionStatistics(statistics);
        MethodInfo add = typeof(PresentMonFrameRateProvider).GetMethod(
            "AddObservation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo sample = typeof(PresentMonFrameRateProvider).GetMethod(
            "CreateCurrentSampleCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Frame(int pid, string chain, double milliseconds) =>
            add.Invoke(provider, [new PresentMonFrame(pid, chain, milliseconds)]);
        FrameRateSample Poll() => (FrameRateSample)sample.Invoke(provider, [null])!;

        Frame(42, "secondary", 100d);
        for (int index = 0; index < 3_000; index++)
        {
            Frame(42, "main", 10d);
        }
        Assert.AreEqual(100d, Poll().FramesPerSecond);
        Assert.AreEqual(3_000, statistics.Snapshot()!.SampleCount);
        Frame(42, "main", 200d);
        Frame(42, "secondary", 500d);
        _ = Poll();
        _ = Poll();
        Assert.AreEqual(3_001, statistics.Snapshot()!.SampleCount);
        Assert.AreEqual(200d, statistics.Snapshot()!.MaximumFrameTimeMilliseconds);
        Assert.AreEqual(1, statistics.Snapshot()!.HitchCount);

        // Zmiana swapchaina i PID zachowuje dotychczasowe klatki sesji.
        for (int index = 0; index < 3_000; index++)
        {
            Frame(7, "replacement", 20d);
        }
        Assert.AreEqual(50d, Poll().FramesPerSecond);
        Assert.AreEqual(6_001, statistics.Snapshot()!.SampleCount);
        _ = Poll();
        Assert.AreEqual(6_001, statistics.Snapshot()!.SampleCount);

        SessionFrameRateStatisticsAccumulator nextSession = new();
        provider.SetSessionStatistics(nextSession);
        Frame(7, "replacement", 20d);
        _ = Poll();
        Assert.AreEqual(1, nextSession.Snapshot()!.SampleCount);
        Assert.AreEqual(6_001, statistics.Snapshot()!.SampleCount);
    }

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
        StringAssert.StartsWith(first, "Dismode-");
        Assert.IsFalse(
            first.Contains(firstSid, StringComparison.Ordinal));
        Assert.AreEqual("Dismode-".Length + 16, first.Length);
    }

    [TestMethod]
    public async Task OverlayAndSessionCaptureUseDifferentEtwSessions()
    {
        // Nakladka (proces UI) i sesja gry (SessionHost) mierza jako ten sam
        // uzytkownik. Ze wspolna nazwa sesji ETW kazdy start PresentMon
        // z --stop_existing_session zatrzymywal pomiar drugiego — zmierzone
        // na zywym procesie: dwa providery przez 25 s nie dostaly ani jednej
        // klatki, choc kazdy osobno mierzyl 240 FPS.
        await using PresentMonFrameRateProvider session = new();
        await using PresentMonFrameRateProvider overlay = new(
            captureRole: PresentMonCaptureRole.Overlay);

        Assert.AreNotEqual(
            session.CaptureSessionName,
            overlay.CaptureSessionName);
    }

    [TestMethod]
    public async Task SessionCaptureKeepsTheNameEarlierReleasesUsed()
    {
        // Nazwa sesji gry zostaje bez zmian: po niej sprzatana jest sierota
        // po wczesniejszym uruchomieniu, a ta mogla zostac po starej wersji.
        await using PresentMonFrameRateProvider session = new();

        Assert.AreEqual(
            PresentMonFrameRateProvider.CreateCaptureSessionName(
                System.Security.Principal.WindowsIdentity.GetCurrent()
                    .User!.Value),
            session.CaptureSessionName);
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
            "Dismode.PresentMonTests",
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

    [TestMethod]
    [Timeout(60_000)]
    public async Task GameThatHasNotRenderedYetIsWaitingNotFailed()
    {
        // Valheim przez pierwsze ~30 s sesji laduje sie i nie wyswietla ani
        // jednej klatki. Pomiar po 16 s oglaszal wtedy blad i obwinial usluge
        // PresentMon firmy Intel — choc ta sama usluga dzialala dalej, a klatki
        // przyszly, gdy gra zaczela rysowac. Brak klatek przy dzialajacym
        // PresentMon to czekanie na gre, nie awaria.
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        string executablePath = FindPinnedPresentMonExecutable();
        ManualTimeProvider time = new();
        await using PresentMonFrameRateProvider provider = new(
            executablePath,
            $"integration-test-{Guid.NewGuid():N}",
            time);

        _ = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));
        _ = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));
        FrameRateSample stillLoading = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);

        Assert.AreEqual(
            FrameRateStatus.WaitingForGame,
            stillLoading.Status,
            stillLoading.Message);
        Assert.IsFalse(
            stillLoading.Message.Contains(
                "Intel",
                StringComparison.Ordinal),
            "Komunikat nie moze obwiniac narzedzia, ktore pomiarowi nie "
                + "przeszkadza.");
        Assert.IsNotNull(
            provider.ActiveCaptureProcessId,
            "PresentMon ma dalej czekac na klatki, a nie zostac zatrzymany.");
    }

    [TestMethod]
    public async Task CaptureThatExitsWithoutFramesIsReportedAndNotRespawnedImmediately()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        string executablePath = FindPinnedPresentMonExecutable();
        ManualTimeProvider time = new();
        await using PresentMonFrameRateProvider provider = new(
            executablePath,
            $"integration-test-{Guid.NewGuid():N}",
            time);

        _ = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);
        int captureProcessId = provider.ActiveCaptureProcessId
            ?? throw new AssertFailedException(
                "Provider nie zapisał własnego procesu capture.");
        try
        {
            using System.Diagnostics.Process capture =
                System.Diagnostics.Process.GetProcessById(captureProcessId);
            capture.Kill();
            await capture.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
            // PresentMon already exited on its own, which is the same case.
        }

        FrameRateSample first = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);
        FrameRateSample second = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);

        Assert.AreEqual(FrameRateStatus.Failed, first.Status);
        Assert.AreEqual(FrameRateStatus.Failed, second.Status);
        Assert.IsNull(provider.ActiveCaptureProcessId);

        time.Advance(TimeSpan.FromSeconds(11));
        FrameRateSample retried = await provider.SampleAsync(
            [harness.Process.Id],
            CancellationToken.None);

        Assert.AreEqual(FrameRateStatus.Starting, retried.Status);
        Assert.IsNotNull(provider.ActiveCaptureProcessId);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static string FindPinnedPresentMonExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "The Dismode solution root could not be located.");
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

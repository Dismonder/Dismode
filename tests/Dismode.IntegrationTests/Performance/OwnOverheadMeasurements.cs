using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dismode.Core.Cpu;
using Dismode.Core.Domain.Processes;
using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryOptimizer.Core.Storage;
using Dismode.MemoryOptimizer.Presentation;
using Dismode.UI.Services;
using Dismode.Windows.Cpu;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
[TestCategory("Perf")]
[DoNotParallelize]
public sealed class OwnOverheadMeasurements
{
    private const string CsvHeader =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime,ClickToPhotonLatency,AllInputToPhotonLatency";
    private const string CsvRow =
        "\"Game, \"\"Test\"\".exe\",42,0xAABBCCDD,DXGI,0,512,1,Hardware: Independent Flip,Application,123.456,4.1667,2.1,2.0667,0.2,3.5,3.0,0.5,4.0,4.1667,NA,NA,NA,NA";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Item1StoppedOverlayDoesNotConsumeFrames()
    {
        await using FakeFrames frames = new();
        await using ForegroundGameWatcher watcher = new(
            frames, foregroundProcess: () => (42, "Game"));
        await watcher.SampleAsync(CancellationToken.None);
        await watcher.SetEnabledAsync(false);
        Measurement result = Measure(frames.ReplaySecond, 100, 1);
        Report("1 stopped consumer, 240 rows/s", result);
        Assert.IsFalse(frames.Active);
        Assert.AreEqual(1, frames.Stops);
        Assert.IsLessThanOrEqualTo(8d, result.Bytes);

        foreach (string reason in new[] { "overlay disabled", "tracking disabled", "managed session" })
        {
            await watcher.SetEnabledAsync(true);
            frames.BlockSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task sample = watcher.SampleAsync(CancellationToken.None).AsTask();
            Task stop = watcher.SetEnabledAsync(false).AsTask();
            Task queuedSample = watcher.SampleAsync(CancellationToken.None).AsTask();
            Assert.IsFalse(stop.IsCompleted, reason);
            frames.BlockSample.SetResult();
            await Task.WhenAll(sample, stop, queuedSample);
            await watcher.SampleAsync(CancellationToken.None);
            Assert.IsFalse(frames.Active, reason);
        }

        Assert.AreEqual(4, frames.Starts);
        Assert.AreEqual(4, frames.Stops);
    }

    [TestMethod]
    public async Task Item2AuxiliaryHistogramsReuseSampleStorage()
    {
        ReplayClock clock = new();
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "perf", clock);
        SessionFrameRateStatisticsAccumulator statistics = new();
        provider.SetSessionStatistics(statistics);
        Action<PresentMonFrame> add = typeof(PresentMonFrameRateProvider)
            .GetMethod("AddObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<PresentMonFrame>>(provider);
        SampleFrames sample = typeof(PresentMonFrameRateProvider)
            .GetMethod("CreateCurrentSampleCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<SampleFrames>(provider);
        int selectedCount = 0;
        void Boundary()
        {
            for (int index = 0; index < 240; index++)
            {
                add(new(42, "main", 4.1667, DisplayTracked: true, DisplayedTimeMilliseconds: 4.1667));
            }

            add(new(42, "auxiliary", 100, DisplayTracked: true, DisplayedTimeMilliseconds: 100));
            _ = sample(out _);
            selectedCount += 240;
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Measurement result = Measure(Boundary, 50, 1);
        Report("2 dominant + auxiliary boundary at 1 Hz", result);
        Assert.IsLessThanOrEqualTo(3_000d, result.Bytes);
        Assert.AreEqual(selectedCount, statistics.Snapshot()!.SampleCount);
        Assert.AreEqual(selectedCount, statistics.SnapshotDisplayed()!.DisplayedFrameCount);
        // Switch dominance: only auxiliary frames since the preceding boundary count.
        for (int index = 0; index < 600; index++)
        {
            add(new(42, "auxiliary", 100));
        }

        _ = sample(out _);
        Assert.AreEqual(selectedCount + 600, statistics.Snapshot()!.SampleCount);
    }

    [TestMethod]
    public async Task Item4FramePruningRetainsTheExactWindow()
    {
        ReplayClock clock = new();
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "perf", clock);
        Action<PresentMonFrame> add = typeof(PresentMonFrameRateProvider)
            .GetMethod("AddObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<PresentMonFrame>>(provider);
        TimeSpan step = TimeSpan.FromTicks(41_667);
        void Frame()
        {
            add(new(42, "main", 4.1667));
            clock.Advance(step);
        }

        for (int index = 0; index < 600; index++)
        {
            Frame();
        }

        Measurement result = Measure(Frame, 100_000, 240);
        Report("4 prune at 240 frames/s", result);
        Assert.IsLessThanOrEqualTo(8d, result.Bytes);
        System.Collections.IDictionary streams = (System.Collections.IDictionary)
            typeof(PresentMonFrameRateProvider)
                .GetField("_observations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(provider)!;
        Assert.HasCount(1, streams);
        System.Collections.IEnumerable queue = (System.Collections.IEnumerable)streams.Values.Cast<object>().Single();
        object[] observations = queue.Cast<object>().ToArray();
        Assert.HasCount(480, observations);
        DateTimeOffset cutoff = clock.GetUtcNow() - step - TimeSpan.FromSeconds(2);
        foreach (object observation in observations)
        {
            DateTimeOffset timestamp = (DateTimeOffset)observation.GetType()
                .GetProperty("ObservedAtUtc")!.GetValue(observation)!;
            Assert.IsTrue(timestamp >= cutoff);
        }

        clock.Advance(TimeSpan.FromSeconds(3));
        add(new(7, "replacement", 20));
        Assert.HasCount(1, streams);
        Assert.HasCount(1, ((System.Collections.IEnumerable)streams.Values.Cast<object>().Single()).Cast<object>().ToArray());
    }

    [TestMethod]
    public async Task Item5ProBalanceCaptureAndFixedTick()
    {
        using NativeProcessFixture native = new();
        using CpuProcessSampler sampler = new(native.Query);
        Measurement capture = Measure(() =>
        {
            IReadOnlyList<CpuProcessSample> samples = sampler.Capture();
            Assert.HasCount(300, samples);
        }, 1_000, 1);
        Report("5 Capture fixed 300 processes at 1 Hz", capture);
        Assert.IsLessThanOrEqualTo(44_000d, capture.Bytes);
        ReplayClock clock = new();
        FixedCpuSource source = new();
        await using ProBalanceSupervisor supervisor = new(
            source, new NoopActuator(), () => source.GameIds,
            timeProvider: clock, foregroundProcessId: () => 100);
        Measurement tick = Measure(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            IReadOnlyList<ProBalanceDecision> decisions = supervisor
                .TickAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            Assert.HasCount(0, decisions);
        }, 1_000, 1);
        Report("5 supervisor + engine fixed 300 processes at 1 Hz", tick);
        Assert.IsLessThanOrEqualTo(93_000d, tick.Bytes);

        FieldInfo bufferField = typeof(CpuProcessSampler)
            .GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SafeHandle originalBuffer = (SafeHandle)bufferField.GetValue(sampler)!;
        IReadOnlyList<CpuProcessSample> oldSnapshot = sampler.Capture();
        Assert.AreSame(originalBuffer, bufferField.GetValue(sampler));
        native.ReuseFirstPid();
        IReadOnlyList<CpuProcessSample> newSnapshot = sampler.Capture();
        Assert.AreEqual("background", oldSnapshot[0].Name);
        Assert.AreEqual("replacement", newSnapshot[0].Name);
        Assert.AreNotEqual(oldSnapshot[0].StartedAtUtc, newSnapshot[0].StartedAtUtc);
        native.UseScreenSaverImage();
        Assert.AreEqual("scrnsave.scr", sampler.Capture()[0].Name);
        native.RequiredLength = 1024 * 1024;
        Assert.HasCount(300, sampler.Capture());
        Assert.IsTrue(originalBuffer.IsClosed);
        SafeHandle grownBuffer = (SafeHandle)bufferField.GetValue(sampler)!;
        Assert.AreNotSame(originalBuffer, grownBuffer);
        native.RequiredLength = 104 * 300;
        _ = sampler.Capture();
        Assert.AreSame(grownBuffer, bufferField.GetValue(sampler));
        sampler.Dispose();
        Assert.IsTrue(grownBuffer.IsClosed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => sampler.Capture());
    }

    [TestMethod]
    public void Item9ProcessTreeReusesScratchWithoutChangingIdentities()
    {
        TreeFixture fixture = new();
        GameProcessTreeSessionTracker tracker = new(
            fixture.Root, identityProvider: fixture, parentMapProvider: fixture);
        Measurement result = Measure(() =>
        {
            GameProcessTreeObservation observation = tracker.ObserveAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert.HasCount(3, observation.RunningProcesses);
            Assert.IsTrue(observation.IsReliable);
        }, 1_000, 1);
        Report("9 tree 300 system processes, 3 game processes, 1 Hz", result);
        Assert.IsLessThanOrEqualTo(1_400d, result.Bytes);
        ProcessIdentity[] snapshot = tracker.GetKnownProcesses().ToArray();
        fixture.ReuseChildPid();
        GameProcessTreeObservation reused = tracker.ObserveAsync(CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert.IsFalse(reused.RunningProcesses.Any(identity => identity.RuntimeKey.ProcessId == 43));
        Assert.HasCount(3, snapshot);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(43), snapshot[1].RuntimeKey.StartedAtUtc);
    }

    private sealed class TreeFixture : IProcessIdentityProvider, IProcessParentMapProvider
    {
        private readonly Dictionary<int, int> _parents = Enumerable.Range(1, 300)
            .ToDictionary(pid => pid, pid => pid is 43 or 44 ? 42 : 0);
        private readonly Dictionary<int, ProcessIdentity> _identities = new();
        internal ProcessIdentity Root => _identities[42];

        internal TreeFixture()
        {
            foreach (int pid in new[] { 42, 43, 44 })
            {
                _identities[pid] = Identity(pid, DateTimeOffset.UnixEpoch.AddSeconds(pid));
            }
        }

        private static ProcessIdentity Identity(int pid, DateTimeOffset started) =>
            new(new(pid, started), @"C:\fixture\game.exe", new string('A', 64),
                null, "S-1-5-21-1-2-3-1001", 1, null);

        internal void ReuseChildPid()
        {
            _identities[43] = Identity(43, DateTimeOffset.UnixEpoch.AddDays(1));
            _parents[43] = 0;
        }

        public IReadOnlyDictionary<int, int> Capture() => _parents;
        public ValueTask<ProcessIdentity?> TryCaptureAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_identities.GetValueOrDefault(processId));
        public ValueTask<ProcessIdentity?> TryCaptureAsync(Process process, CancellationToken cancellationToken) =>
            TryCaptureAsync(process.Id, cancellationToken);
        public ValueTask<bool> MatchesRuntimeIdentityAsync(ProcessIdentity expectedIdentity, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_identities.TryGetValue(expectedIdentity.RuntimeKey.ProcessId, out ProcessIdentity? actual)
                && actual.MatchesRuntimeIdentity(expectedIdentity));
    }

    [TestMethod]
    public void Item8OverlayRevalidatesCachedWindowOwnership()
    {
        ReplayClock clock = new();
        int probes = 0;
        bool valid = true;
        OverlayTargetWindow target = new(
            resolve: pid =>
            {
                // Same Process/HasExited/MainWindowHandle path, current test process only.
                using Process process = Process.GetProcessById(Environment.ProcessId);
                _ = process.HasExited;
                _ = process.MainWindowHandle;
                probes++;
                return 77;
            },
            isOwnedWindow: (_, _) => valid,
            clock: clock);
        Measurement result = Measure(() =>
        {
            Assert.AreEqual((nint)77, target.Resolve(42));
            clock.Advance(TimeSpan.FromMilliseconds(200));
        }, 1_000, 5);
        Report("8 overlay HWND refresh at 5 Hz", result);
        Assert.IsLessThanOrEqualTo(192d, result.Bytes);
        int previous = probes;
        valid = false;
        _ = target.Resolve(42);
        Assert.AreEqual(previous + 1, probes);
        previous = probes;
        _ = target.Resolve(7);
        Assert.AreEqual(previous + 1, probes);
    }

    [TestMethod]
    public async Task Item6MemorySettingsStayFreshAcrossWriters()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Dismode.OwnOverhead", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "memory.db");
        try
        {
            await using MemoryOptimizerStore store = new(path);
            await store.InitializeAsync(CancellationToken.None);
            MemoryOptimizerSettings settings = new()
            {
                ExcludedProcesses = Enumerable.Range(1, 32).Select(index => $"fixture{index}").ToArray(),
            };
            await store.SaveSettingsAsync(settings, CancellationToken.None);
            MemoryRefreshPolicy policy = new() { IsVisible = false };
            double interval = policy.StatusPollInterval.TotalSeconds;
            Assert.AreEqual(30d, interval);
            Measurement result = await MeasureAsync(async () =>
            {
                MemoryOptimizerSettings current = await store.GetSettingsAsync(CancellationToken.None);
                Assert.HasCount(32, current.ExcludedProcesses);
            }, 1_000, 1 / interval + 1d / 15);
            Report("6 settings read, hidden tray + 15 s automation", result);
            Assert.IsLessThanOrEqualTo(1_700d, result.Bytes);
            await using MemoryOptimizerStore other = new(path);
            await other.SaveSettingsAsync(settings with { AutomationPaused = true }, CancellationToken.None);
            Assert.IsTrue((await store.GetSettingsAsync(CancellationToken.None)).AutomationPaused);
            await store.SaveSettingsAsync(settings with { AutomationPaused = false }, CancellationToken.None);
            Assert.IsFalse((await store.GetSettingsAsync(CancellationToken.None)).AutomationPaused);
            policy.IsVisible = true;
            Assert.AreEqual(TimeSpan.FromSeconds(5), policy.StatusPollInterval);
            Assert.IsTrue(policy.TryBeginStatus());
            policy.EndStatus();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Measurement> MeasureAsync(Func<Task> action, int iterations, double callsPerSecond)
    {
        for (int index = 0; index < 16; index++)
        {
            await action();
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++)
        {
            await action();
        }

        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        return new((GC.GetTotalAllocatedBytes(precise: true) - allocated) / (double)iterations,
            milliseconds, callsPerSecond);
    }

    [TestMethod]
    public void Item7IdleDetectionCachesProfiles()
    {
        DetectionProfile[] profiles = Enumerable.Range(1, 64)
            .Select(index => new DetectionProfile(@$"C:\Games\{index}\game{index}.exe", true)).ToArray();
        GameDetectionProfileIndex<DetectionProfile> index = new(
            static profile => profile.Enabled, static profile => profile.Path);
        Measurement result = Measure(() =>
        {
            Dictionary<string, List<DetectionProfile>> matches = index.Get(profiles);
            Assert.HasCount(64, matches);
            Assert.AreEqual(profiles[0], matches["game1"][0]);
        }, 10_000, 1d / 6);
        Report("7 profile index, 64 games at 6 s cadence", result);
        Assert.IsLessThanOrEqualTo(8d, result.Bytes);
        profiles[0] = profiles[0] with { Enabled = false };
        index.Invalidate();
        Assert.IsFalse(index.Get(profiles).ContainsKey("game1"));
        profiles[0] = new(@"C:\Games\replacement.exe", true);
        index.Invalidate();
        Assert.IsTrue(index.Get(profiles).ContainsKey("replacement"));

        GameDetectionScanCadence cadence = new();
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        TimeSpan nominal = TimeSpan.FromSeconds(6);
        Assert.IsTrue(cadence.TryBegin(now, nominal));
        cadence.Request();
        Assert.IsFalse(cadence.TryBegin(now.AddSeconds(1), nominal));
        cadence.End();
        Assert.IsFalse(cadence.TryBegin(now.AddMilliseconds(999), nominal));
        Assert.IsTrue(cadence.TryBegin(now.AddSeconds(1), nominal));
        cadence.End();
        Assert.IsFalse(cadence.TryBegin(now.AddSeconds(6), nominal));
        Assert.IsTrue(cadence.TryBegin(now.AddSeconds(7), nominal));
        cadence.End();
    }

    private sealed record DetectionProfile(string Path, bool Enabled);

    private sealed class FixedCpuSource : ICpuProcessSource
    {
        private readonly CpuProcessSample[] _samples = Enumerable.Range(1, 300)
            .Select(pid => new CpuProcessSample(pid, "background", DateTimeOffset.UnixEpoch,
                TimeSpan.Zero, pid > 100 && pid < 110 ? 100 : 0, 1)).ToArray();
        internal IReadOnlySet<int> GameIds { get; } = new HashSet<int> { 42 };
        public IReadOnlyList<CpuProcessSample> Capture() => _samples;
    }

    private sealed class NoopActuator : IProBalanceActuator
    {
        public ValueTask<bool> RestrainAsync(ProcessRuntimeKey runtimeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
        public ValueTask<bool> ReleaseAsync(ProcessRuntimeKey runtimeKey, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class NativeProcessFixture : IDisposable
    {
        private readonly byte[] _entries = new byte[104 * 300];
        private readonly nint _name = Marshal.StringToHGlobalUni("background.exe");
        private readonly nint _replacementName = Marshal.StringToHGlobalUni("replacement.exe");
        private readonly nint _screenSaverName = Marshal.StringToHGlobalUni("scrnsave.scr");
        internal uint RequiredLength { get; set; } = 104 * 300;

        internal NativeProcessFixture()
        {
            for (int index = 0; index < 300; index++)
            {
                Span<byte> entry = _entries.AsSpan(index * 104, 104);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, index == 299 ? 0u : 104u);
                BinaryPrimitives.WriteInt64LittleEndian(entry[32..], DateTimeOffset.UnixEpoch.UtcDateTime.ToFileTime());
                BinaryPrimitives.WriteUInt16LittleEndian(entry[56..], 28);
                BinaryPrimitives.WriteInt64LittleEndian(entry[64..], _name);
                BinaryPrimitives.WriteInt64LittleEndian(entry[80..], index + 1);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[100..], 1);
            }
        }

        internal int Query(int informationClass, nint buffer, uint length, out uint required)
        {
            required = RequiredLength;
            if (length < required)
            {
                return unchecked((int)0xC0000004);
            }

            Marshal.Copy(_entries, 0, buffer, _entries.Length);
            return 0;
        }

        internal void ReuseFirstPid()
        {
            BinaryPrimitives.WriteInt64LittleEndian(_entries.AsSpan(32),
                DateTimeOffset.UnixEpoch.AddDays(1).UtcDateTime.ToFileTime());
            BinaryPrimitives.WriteInt64LittleEndian(_entries.AsSpan(64), _replacementName);
            BinaryPrimitives.WriteUInt16LittleEndian(_entries.AsSpan(56), 30);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_name);
            Marshal.FreeHGlobal(_replacementName);
            Marshal.FreeHGlobal(_screenSaverName);
        }

        internal void UseScreenSaverImage()
        {
            BinaryPrimitives.WriteInt64LittleEndian(_entries.AsSpan(32),
                DateTimeOffset.UnixEpoch.AddDays(2).UtcDateTime.ToFileTime());
            BinaryPrimitives.WriteInt64LittleEndian(_entries.AsSpan(64), _screenSaverName);
            BinaryPrimitives.WriteUInt16LittleEndian(_entries.AsSpan(56), 24);
        }
    }

    private delegate FrameRateSample SampleFrames(out string? summary);

    [TestMethod]
    public void Item3CsvRowsParseWithoutPerColumnStrings()
    {
        PresentMonCsvParser parser = new(trackDisplay: true);
        _ = parser.TryParse("\uFEFF" + CsvHeader, out _);
        string[] corpus =
        [
            CsvRow,
            CsvRow.Replace(",4.1667,NA,NA,NA,NA", ",NA,NA,NA,NA,NA", StringComparison.Ordinal),
            CsvRow.Replace(",4.1667,NA,NA,NA,NA", ",,NA,NA,NA,NA", StringComparison.Ordinal),
            CsvRow.Replace(",4.1667,2.1", ",NA,2.1", StringComparison.Ordinal),
            CsvRow.Replace("0xAABBCCDD", "\"0xAABBCCDD\"", StringComparison.Ordinal),
            CsvRow.Replace("0xAABBCCDD", "\"chain,\"\"quoted\"\"\"", StringComparison.Ordinal),
        ];
        Assert.IsTrue(parser.HasValidHeader);
        Assert.IsTrue(parser.TryParse(corpus[0], out PresentMonFrame shown));
        Assert.AreEqual(42, shown.ProcessId);
        Assert.AreEqual("0xAABBCCDD", shown.SwapChainAddress);
        Assert.AreEqual(4.1667, shown.FrameTimeMilliseconds);
        Assert.AreEqual(3d, shown.GpuBusyMilliseconds);
        Assert.AreEqual(4.1667, shown.DisplayedTimeMilliseconds);
        Assert.IsTrue(parser.TryParse(corpus[1], out PresentMonFrame na));
        Assert.IsTrue(na.IsDropped);
        Assert.IsTrue(parser.TryParse(corpus[2], out PresentMonFrame empty));
        Assert.IsTrue(empty.IsDropped);
        Assert.IsFalse(parser.TryParse(corpus[3], out _));
        Assert.IsTrue(parser.TryParse(corpus[4], out PresentMonFrame quoted));
        Assert.AreEqual(shown, quoted);
        Assert.IsTrue(parser.TryParse(corpus[5], out PresentMonFrame escaped));
        Assert.AreEqual("chain,\"quoted\"", escaped.SwapChainAddress);
        Measurement result = Measure(() =>
        {
            bool valid = parser.TryParse(CsvRow, out PresentMonFrame frame);
            if (!valid || frame.ProcessId != 42)
            {
                Assert.Fail("CSV replay diverged.");
            }
        }, 100_000, 240);
        Report("3 PresentMon 2.5.1 row at 240 Hz", result);
        Assert.IsLessThanOrEqualTo(8d, result.Bytes);
    }

    private sealed class ReplayClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static Measurement Measure(Action action, int iterations, double callsPerSecond)
    {
        for (int index = 0; index < 16; index++)
        {
            action();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int index = 0; index < iterations; index++)
        {
            action();
        }

        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        double bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)iterations;
        return new(bytes, milliseconds, callsPerSecond);
    }

    private void Report(string item, Measurement value) =>
        TestContext.WriteLine(
            $"{item}: {value.Bytes:F2} B/call; {value.Milliseconds:F6} ms/call; " +
            $"{value.Bytes * value.CallsPerSecond:F2} B/s; " +
            $"{value.Milliseconds * value.CallsPerSecond:F6} ms/s");

    private readonly record struct Measurement(double Bytes, double Milliseconds, double CallsPerSecond);

    private sealed class FakeFrames : IFrameRateProvider
    {
        private readonly PresentMonCsvParser _parser = new(trackDisplay: true);
        internal bool Active { get; private set; }
        internal int Stops { get; private set; }
        internal int Starts { get; private set; }
        internal TaskCompletionSource? BlockSample { get; set; }

        internal FakeFrames() => _ = _parser.TryParse(CsvHeader, out _);

        public async ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds, CancellationToken cancellationToken)
        {
            if (!Active)
            {
                Starts++;
            }

            Active = true;
            if (BlockSample is { } pending)
            {
                await pending.Task;
            }

            return new(FrameRateStatus.Measuring, 240, 4.1667, 42, "fixture");
        }

        internal void ReplaySecond()
        {
            if (Active)
            {
                for (int index = 0; index < 240; index++)
                {
                    _ = _parser.TryParse(CsvRow, out _);
                }
            }
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            Active = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    }
}

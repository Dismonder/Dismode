using System.Diagnostics;
using System.Reflection;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.SystemOptimization;
using Dismode.Core.SystemOptimization;
using Dismode.SystemAgent.Ipc;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class PresentMonCaptureLossTests
{
    private const string Header = "Application,ProcessID,SwapChainAddress,FrameTime,DisplayedTime";
    private const string Row = "Game.exe,42,0xA,10.0,NA";

    [TestMethod]
    [DataRow("", BenchmarkCaptureLossState.None, 0L, 0L)]
    [DataRow("Processing completed.\nwarning: 0 ETW events were lost.\nwarning: 0 ETW buffers were lost.", BenchmarkCaptureLossState.None, 0L, 0L)]
    [DataRow("warning: 24000 ETW events were lost.", BenchmarkCaptureLossState.Confirmed, 24000L, 0L)]
    [DataRow("warning: 7 ETW buffers were lost.", BenchmarkCaptureLossState.Confirmed, 0L, 7L)]
    [DataRow("warning: ETW buffer overflow while processing events.", BenchmarkCaptureLossState.Unknown, 0L, 0L)]
    [DataRow("warning: unknown diagnostic.\nProcessing completed.\nwarning: 0 ETW events were lost.", BenchmarkCaptureLossState.Unknown, 0L, 0L)]
    [DataRow("warning: 4294967296 ETW events were lost.", BenchmarkCaptureLossState.Unknown, 0L, 0L)]
    [DataRow("warning: 24000 ETW events were lost.\nwarning: 24000 ETW events were lost.\nwarning: 7 ETW buffers were lost.\nProcessing completed.\nwarning: 0 ETW events were lost.\nwarning: unknown diagnostic.", BenchmarkCaptureLossState.Confirmed, 24000L, 7L)]
    public async Task StderrFixturesPreserveLossStateWithoutAffectingSessionFps(
        string diagnostics, BenchmarkCaptureLossState expected, long events, long buffers)
    {
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "loss-test");
        BenchmarkFrameCollector collector = provider.BeginBenchmarkCollection(42);
        using StringReader stderr = new(diagnostics);
        await provider.ReadStandardErrorAsync(stderr, CancellationToken.None);
        using StringReader stdout = new(Header + "\n" + Row + "\n");
        await provider.ReadStandardOutputAsync(stdout, [42], CancellationToken.None);
        PresentMonBenchmarkCapture capture = collector.SnapshotDominantCapture();
        Assert.IsNotNull(capture.CaptureLoss);
        Assert.AreEqual(expected, capture.CaptureLoss.State);
        Assert.AreEqual(events, capture.CaptureLoss.LostEvents);
        Assert.AreEqual(buffers, capture.CaptureLoss.LostBuffers);
        Assert.HasCount(1, capture.FrameTimesMilliseconds);

        // DisplayedTime=NA is a dropped frame, not a lost ETW event.
        MethodInfo sample = typeof(PresentMonFrameRateProvider).GetMethod(
            "CreateCurrentSampleCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FrameRateSample fps = (FrameRateSample)sample.Invoke(provider, [null])!;
        Assert.AreEqual(FrameRateStatus.Measuring, fps.Status);
        Assert.AreEqual(100d, fps.FramesPerSecond);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LossBeforeBenchmarkIsExcludedWhileLossInsideInvalidates(bool lossInside)
    {
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "loss-test");
        using StringReader stderr = new("warning: 24000 ETW events were lost.");
        await provider.ReadStandardErrorAsync(stderr, CancellationToken.None);
        BenchmarkFrameCollector collector = provider.BeginBenchmarkCollection(42);
        string diagnostic = lossInside ? "warning: 3 ETW buffers were lost." : "Processing completed.";
        using StringReader stdout = new(Header + "\n" + string.Join('\n', Enumerable.Repeat(Row, 6000)) + "\n" + diagnostic);
        await provider.ReadStandardOutputAsync(stdout, [42], CancellationToken.None);
        PresentMonBenchmarkCapture capture = collector.SnapshotDominantCapture();
        Assert.AreEqual(0L, capture.CaptureLoss!.LostEvents);
        Assert.AreEqual(lossInside ? 3L : 0L, capture.CaptureLoss.LostBuffers);
        collector.RecordLoss(BenchmarkCaptureLoss.None);
        Assert.AreEqual(lossInside ? BenchmarkCaptureLossState.Confirmed : BenchmarkCaptureLossState.None,
            collector.SnapshotDominantCapture().CaptureLoss!.State);
        BenchmarkMetrics metrics = BenchmarkAnalyzer.Analyze(new(
            Guid.NewGuid(), BenchmarkVariant.Baseline, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60), capture.FrameTimesMilliseconds,
            null, null, null, "fixture", capture.CaptureLoss));
        Assert.AreEqual(!lossInside, metrics.IsValid);
    }

    [TestMethod]
    public async Task TerminalLossIsDrainedBeforeCancellationAndFrameCollectionStaysFrozen()
    {
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "loss-test");
        BenchmarkFrameCollector collector = provider.BeginBenchmarkCollection(42);
        using StringReader frames = new(Header + "\n" + Row);
        await provider.ReadStandardOutputAsync(frames, [42], CancellationToken.None);
        collector.StopCollectingFrames();
        using TerminalDiagnosticReader tail = new();
        using CancellationTokenSource lifetime = new();
        Task diagnostics = provider.ReadStandardErrorAsync(tail, lifetime.Token);
        Task<Exception?> drain = PresentMonFrameRateProvider.DrainCaptureReadersAsync(
            Task.CompletedTask, diagnostics, lifetime).AsTask();
        try
        {
            Assert.IsFalse(drain.IsCompleted);
            Assert.IsFalse(lifetime.IsCancellationRequested);
        }
        finally
        {
            tail.Release.SetResult("warning: 24000 ETW events were lost.");
        }

        Assert.IsNull(await drain);
        Assert.IsTrue(lifetime.IsCancellationRequested);
        collector.Add(new(42, "0xA", 20));
        PresentMonBenchmarkCapture capture = collector.SnapshotDominantCapture();
        Assert.HasCount(1, capture.FrameTimesMilliseconds);
        Assert.AreEqual(24000L, capture.CaptureLoss!.LostEvents);
    }

    [TestMethod]
    public async Task ReadersWithoutEofAreCancelledAfterGraceWithoutCleanupFailure()
    {
        await using PresentMonFrameRateProvider provider = new("fixture.exe", "loss-test");
        using NeverEndingReader stdout = new();
        using NeverEndingReader stderr = new();
        using CancellationTokenSource lifetime = new();
        Task frames = provider.ReadStandardOutputAsync(stdout, [42], lifetime.Token);
        Task diagnostics = provider.ReadStandardErrorAsync(stderr, lifetime.Token);
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            Exception? failure = await PresentMonFrameRateProvider.DrainCaptureReadersAsync(
                frames, diagnostics, lifetime).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsNull(failure);
            Assert.IsTrue(lifetime.IsCancellationRequested);
            Assert.IsTrue(frames.IsCompletedSuccessfully);
            Assert.IsTrue(diagnostics.IsCompletedSuccessfully);
            Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromMilliseconds(450));
        }
        finally
        {
            lifetime.Cancel();
            await Task.WhenAll(frames, diagnostics);
        }
    }

    [TestMethod]
    public async Task ReaderFaultStillProducesCleanupFailure()
    {
        using CancellationTokenSource lifetime = new();
        InvalidOperationException failure = new("fixture reader failure");
        Assert.AreSame(failure, await PresentMonFrameRateProvider.DrainCaptureReadersAsync(
            Task.FromException(failure), Task.CompletedTask, lifetime));
        Assert.IsTrue(lifetime.IsCancellationRequested);
    }

    [TestMethod]
    public void GrpcRoundTripsLossAndOldRequestsRemainUnchanged()
    {
        MethodInfo parse = typeof(SystemOptimizerGrpcService).GetMethod(
            "ParseCapture", BindingFlags.NonPublic | BindingFlags.Static)!;
        SubmitBenchmarkCaptureRequest request = new()
        {
            CaptureId = Guid.NewGuid().ToString("D"),
            ProfileStateHash = "fixture",
            CaptureLoss = new()
            {
                State = (BenchmarkCaptureLossStateValue)(int)BenchmarkCaptureLossState.Confirmed,
                LostEvents = 24000,
            },
        };
        SubmitBenchmarkCaptureRequest wire = SubmitBenchmarkCaptureRequest.Parser.ParseFrom(
            Google.Protobuf.MessageExtensions.ToByteArray(request));
        BenchmarkCapture capture = (BenchmarkCapture)parse.Invoke(null, [wire])!;
        Assert.AreEqual(24000L, capture.CaptureLoss!.LostEvents);
        wire.CaptureLoss = null;
        Assert.IsNull(((BenchmarkCapture)parse.Invoke(null, [wire])!).CaptureLoss);
        request.CaptureLoss.LostEvents = -1;
        TargetInvocationException failure = Assert.ThrowsExactly<TargetInvocationException>(
            () => parse.Invoke(null, [request]));
        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(failure.InnerException);
    }

    private sealed class NeverEndingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class TerminalDiagnosticReader : TextReader
    {
        private bool _read;
        internal TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_read)
            {
                return null;
            }

            _read = true;
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }
}

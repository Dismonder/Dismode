using Dismode.UI.Services;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class ForegroundGameWatcherTests
{
    [TestMethod]
    [DataRow(FrameRateStatus.MissingComponent)]
    [DataRow(FrameRateStatus.InvalidComponent)]
    [DataRow(FrameRateStatus.AccessDenied)]
    [DataRow(FrameRateStatus.Failed)]
    public async Task FailedCaptureKeepsForegroundTargetForRetryWithoutForegroundChange(FrameRateStatus status)
    {
        FakeFrames frames = new() { Status = status };
        await using ForegroundGameWatcher watcher = new(frames, foregroundProcess: () => (42, "Game"));

        ForegroundGameSample failed = await watcher.SampleAsync(CancellationToken.None);
        Assert.AreEqual(42, failed.ProcessId);
        Assert.IsFalse(failed.IsPresenting);
        Assert.AreEqual(1, frames.Stops);

        Assert.AreEqual(42, (await watcher.SampleAsync(CancellationToken.None)).ProcessId);
        Assert.AreEqual(2, frames.Stops);

        frames.Status = FrameRateStatus.Measuring;
        ForegroundGameSample resumed = await watcher.SampleAsync(CancellationToken.None);
        Assert.AreEqual(42, resumed.ProcessId);
        Assert.IsTrue(resumed.IsPresenting);
    }

    [TestMethod]
    public async Task TransientCaptureExceptionDetachesAndAllowsRetry()
    {
        FakeFrames frames = new() { ThrowOnSample = true };
        await using ForegroundGameWatcher watcher = new(frames, foregroundProcess: () => (42, "Game"));
        ForegroundGameSample failed = await watcher.SampleAsync(CancellationToken.None);
        Assert.AreEqual(42, failed.ProcessId);
        Assert.IsFalse(failed.IsPresenting);
        Assert.AreEqual(1, frames.Stops);
        frames.ThrowOnSample = false;
        Assert.IsTrue((await watcher.SampleAsync(CancellationToken.None)).IsPresenting);
    }

    [TestMethod]
    public async Task DesktopAndDisabledOverlayDoNotSampleAndStartingGameKeepsItsTarget()
    {
        int? foreground = 42;
        FakeFrames frames = new() { Status = FrameRateStatus.Starting };
        await using ForegroundGameWatcher watcher = new(frames, foregroundProcess: () => (foreground, "Game"));
        ForegroundGameSample starting = await watcher.SampleAsync(CancellationToken.None);
        Assert.AreEqual(42, starting.ProcessId);
        Assert.IsFalse(starting.IsPresenting);

        foreground = null;
        Assert.IsNull((await watcher.SampleAsync(CancellationToken.None)).ProcessId);
        Assert.AreEqual(1, frames.Samples);
        Assert.AreEqual(1, frames.Stops);

        foreground = 42;
        await watcher.SetEnabledAsync(false);
        _ = await watcher.SampleAsync(CancellationToken.None);
        Assert.AreEqual(1, frames.Samples);
    }

    private sealed class FakeFrames : IFrameRateProvider
    {
        internal FrameRateStatus Status { get; set; } = FrameRateStatus.Measuring;
        internal bool ThrowOnSample { get; set; }
        internal int Stops { get; private set; }
        internal int Samples { get; private set; }

        public ValueTask<FrameRateSample> SampleAsync(IReadOnlyCollection<int> processIds, CancellationToken cancellationToken)
        {
            Samples++;
            if (ThrowOnSample)
            {
                throw new IOException("fixture");
            }

            return ValueTask.FromResult(new FrameRateSample(Status,
                Status == FrameRateStatus.Measuring ? 60 : null,
                Status == FrameRateStatus.Measuring ? 16.67 : null, 42, "fixture"));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

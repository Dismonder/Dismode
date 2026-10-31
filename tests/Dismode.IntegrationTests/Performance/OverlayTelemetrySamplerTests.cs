using Dismode.IntegrationTests.Cpu;
using Dismode.Windows.Cpu;
using Dismode.Windows.Gpu;
using Dismode.Windows.Performance;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class OverlayTelemetrySamplerTests
{
    [TestMethod]
    public void HiddenConsumerDoesNotReadAndVisibleConsumerRunsAtOneHertz()
    {
        LogicalProcessorLoadSamplerTests.FakeProcessorTimes cpu = new();
        using GpuTelemetrySamplerTests.FakeGpuCounters gpu = new();
        TestClock clock = new();
        using OverlayTelemetrySession sampler = new(new(cpu), new(gpu), clock);
        for (int index = 0; index < 10; index++)
        {
            Assert.AreEqual(default, sampler.Sample(false, 42));
        }

        Assert.AreEqual(0, cpu.Reads);
        Assert.AreEqual(0, gpu.Reads);
        _ = sampler.Sample(true, 42);
        cpu.Advance();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.IsNull(sampler.Sample(true, 42).Cpu);
        Assert.AreEqual(1, cpu.Reads);
        Assert.AreEqual(1, gpu.Reads);
        clock.Advance(TimeSpan.FromMilliseconds(800));
        Assert.IsNotNull(sampler.Sample(true, 42).Cpu);
        Assert.AreEqual(2, cpu.Reads);
        Assert.AreEqual(2, gpu.Reads);
        _ = sampler.Sample(false, 42);
        int stops = gpu.Stops;
        _ = sampler.Sample(false, 42);
        Assert.AreEqual(stops, gpu.Stops);
        cpu.Advance();
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsNull(sampler.Sample(true, 42).Cpu);
        Assert.AreEqual(3, cpu.Reads);
        Assert.IsNull(sampler.Sample(true, 43).Cpu);
        Assert.AreEqual(4, cpu.Reads);
    }

    [TestMethod]
    public async Task WorkerSamplesOffCallerThreadAndStopsAfterInflightRead()
    {
        LogicalProcessorLoadSamplerTests.FakeProcessorTimes cpu = new();
        using BlockingGpuCounters gpu = new();
        await using OverlayTelemetrySampler sampler = new(new(cpu), new(gpu), TimeProvider.System);
        sampler.SetActive(false, 42);
        Assert.AreEqual(default, sampler.Snapshot);
        Assert.AreEqual(0, cpu.Reads);
        try
        {
            int caller = Environment.CurrentManagedThreadId;
            sampler.SetActive(true, 42);
            Assert.IsTrue(gpu.Entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreNotEqual(caller, cpu.ThreadId);
            Assert.AreEqual(cpu.ThreadId, gpu.ThreadId);
            for (int index = 0; index < 100; index++)
            {
                sampler.SetActive(true, 42);
                Assert.AreEqual(default, sampler.Snapshot);
            }

            sampler.SetActive(false, null);
            Assert.IsFalse(gpu.Stopped.IsSet);
            Assert.AreEqual(default, sampler.Snapshot);
            gpu.Release.Set();
            Assert.IsTrue(gpu.Stopped.Wait(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(gpu.StopDuringRead);
            Assert.AreEqual(1, gpu.Reads);
            Assert.AreEqual(default, sampler.Snapshot);
            await sampler.DisposeAsync();
            Assert.IsTrue(gpu.Disposed);
            Assert.AreEqual(gpu.ThreadId, gpu.DisposeThreadId);
        }
        finally
        {
            gpu.Release.Set();
        }
    }

    [TestMethod]
    public void UnexpectedExceptionIsLoggedAndRestartHonorsThirtySecondBackoff()
    {
        LogicalProcessorLoadSamplerTests.FakeProcessorTimes cpu = new();
        using ThrowingGpuCounters gpu = new();
        TestClock clock = new();
        int logged = 0;
        using OverlayTelemetrySession session = new(new(cpu), new(gpu), clock, _ => logged++);
        Assert.AreEqual(default, session.Sample(true, 42));
        Assert.AreEqual(1, logged);
        Assert.AreEqual(1, gpu.Reads);
        _ = session.Sample(false, 42);
        _ = session.Sample(true, 42);
        Assert.AreEqual(1, gpu.Reads);
        clock.Advance(TimeSpan.FromSeconds(29));
        _ = session.Sample(true, 42);
        Assert.AreEqual(1, gpu.Reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        _ = session.Sample(true, 42);
        Assert.AreEqual(2, gpu.Reads);
        Assert.AreEqual(2, logged);
    }

    [TestMethod]
    public async Task DisposeWaitsForInflightSampleAndDoesNotPublishItsResult()
    {
        using BlockingGpuCounters gpu = new();
        await using OverlayTelemetrySampler sampler = new(new(new LogicalProcessorLoadSamplerTests.FakeProcessorTimes()),
            new(gpu), TimeProvider.System);
        try
        {
            sampler.SetActive(true, 42);
            Assert.IsTrue(gpu.Entered.Wait(TimeSpan.FromSeconds(5)));
            Task disposal = sampler.DisposeAsync().AsTask();
            Assert.IsFalse(disposal.IsCompleted);
            Assert.IsFalse(gpu.Disposed);
            Assert.AreEqual(default, sampler.Snapshot);
            gpu.Release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(gpu.Disposed);
            Assert.AreEqual(gpu.ThreadId, gpu.DisposeThreadId);
            Assert.AreEqual(default, sampler.Snapshot);
        }
        finally
        {
            gpu.Release.Set();
        }
    }

    private sealed class ThrowingGpuCounters : IGpuCounterSource
    {
        internal int Reads { get; private set; }
        public bool TryRead(out ReadOnlySpan<GpuCounterReading> readings)
        {
            Reads++;
            throw new ArithmeticException("synthetic failure");
        }

        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class BlockingGpuCounters : IGpuCounterSource
    {
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal ManualResetEventSlim Stopped { get; } = new();
        internal int Reads { get; private set; }
        internal int ThreadId { get; private set; }
        internal int DisposeThreadId { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool StopDuringRead { get; private set; }
        private bool _reading;
        private readonly GpuCounterReading[] _readings =
        [new(new(new(1, 0), 0, 0, default, GpuCounterKind.AdapterMemory), 512, 0, true)];

        public bool TryRead(out ReadOnlySpan<GpuCounterReading> readings)
        {
            _reading = true;
            Reads++;
            ThreadId = Environment.CurrentManagedThreadId;
            Entered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Blocked telemetry read was not released.");
                }

                readings = _readings;
                return true;
            }
            finally
            {
                _reading = false;
            }
        }

        public void Stop()
        {
            StopDuringRead |= _reading;
            Stopped.Set();
        }

        public void Dispose()
        {
            Disposed = true;
            DisposeThreadId = Environment.CurrentManagedThreadId;
        }
    }

    internal sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}

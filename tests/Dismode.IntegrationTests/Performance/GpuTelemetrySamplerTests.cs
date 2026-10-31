using Dismode.Windows.Gpu;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class GpuTelemetrySamplerTests
{
    private static readonly GpuAdapterId AdapterA = new(1, 0);
    private static readonly GpuAdapterId AdapterB = new(2, 0);

    [TestMethod]
    public void NewProcessContextDoesNotInvalidateAnExistingThreeDEngine()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Readings = [Engine(AdapterA, 42, 0, 0)];
        _ = sampler.Sample(42);
        source.Readings = [Engine(AdapterA, 42, 500, 1000), Engine(AdapterA, 99, 900, 1000)];
        Assert.AreEqual(50d, sampler.Sample(42)!.Value.ThreeDPercent);
        source.Readings = [Engine(AdapterA, 42, 1000, 2000), Engine(AdapterA, 99, 1000, 2000)];
        Assert.AreEqual(60d, sampler.Sample(42)!.Value.ThreeDPercent);
    }

    [TestMethod]
    public void OnlyFirstSeenInstancesAfterBaselineReportZeroNotNull()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Readings = [Engine(AdapterA, 42, 0, 0)];
        _ = sampler.Sample(42);
        source.Readings = [Engine(AdapterA, 99, 900, 1000)];
        Assert.AreEqual(0d, sampler.Sample(42)!.Value.ThreeDPercent);
    }

    [TestMethod]
    public void UnsupportedAndRemovedAdaptersNeverReuseStaleValues()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Supported = false;
        Assert.IsNull(sampler.Sample(42));
        source.Supported = true;
        source.Readings = [Engine(AdapterA, 42, 0, 0), Memory(AdapterA, 512)];
        _ = sampler.Sample(42);
        source.Readings[0] = Engine(AdapterA, 42, 500, 1000);
        Assert.AreEqual(50d, sampler.Sample(42)!.Value.ThreeDPercent);
        source.Readings = [];
        Assert.IsNull(sampler.Sample(42));
        source.Readings = [Engine(AdapterA, 42, 1000, 2000)];
        Assert.IsNull(sampler.Sample(42));
    }

    [TestMethod]
    public void ResetWrapAndInvalidStatusDoNotProduceUtilizationSpikes()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Readings = [Engine(AdapterA, 42, 1000, 2000)];
        _ = sampler.Sample(42);
        source.Readings[0] = Engine(AdapterA, 42, 10, 3000);
        Assert.IsNull(sampler.Sample(42));
        source.Readings[0] = Engine(AdapterA, 42, 510, 4000);
        Assert.AreEqual(50d, sampler.Sample(42)!.Value.ThreeDPercent);
        source.Readings[0] = Engine(AdapterA, 42, 1000, 100);
        Assert.IsNull(sampler.Sample(42));
        source.Readings[0] = Engine(AdapterA, 42, -1, 200);
        Assert.IsNull(sampler.Sample(42));
        source.Readings[0] = Engine(AdapterA, 42, 1100, 300) with { Valid = false };
        Assert.IsNull(sampler.Sample(42));
        source.Readings[0] = Engine(AdapterA, 42, 1200, 400);
        Assert.IsNull(sampler.Sample(42));
    }

    [TestMethod]
    public void GameAdapterWinsOtherwiseHighestThreeDAndEnginesAreNotSummed()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Readings =
        [
            Engine(AdapterA, 42, 0, 0), Engine(AdapterA, 7, 0, 0),
            Engine(AdapterA, 7, 0, 0, engineId: 1), Engine(AdapterB, 7, 0, 0),
            Engine(AdapterA, 7, 0, 0, type: GpuEngineType.Copy),
            Memory(AdapterA, 512), Memory(AdapterB, 1024),
        ];
        _ = sampler.Sample(42);
        source.Readings[0] = Engine(AdapterA, 42, 100, 1000);
        source.Readings[1] = Engine(AdapterA, 7, 200, 1000);
        source.Readings[2] = Engine(AdapterA, 7, 400, 1000, engineId: 1);
        source.Readings[3] = Engine(AdapterB, 7, 900, 1000);
        source.Readings[4] = Engine(AdapterA, 7, 700, 1000, type: GpuEngineType.Copy);
        GpuTelemetrySnapshot game = sampler.Sample(42)!.Value;
        Assert.AreEqual(AdapterA, game.Adapter);
        Assert.AreEqual(40d, game.ThreeDPercent);
        Assert.AreEqual(70d, game.CopyPercent);
        Assert.AreEqual(512UL, game.DedicatedBytes);
        source.Advance();
        GpuTelemetrySnapshot other = sampler.Sample(null)!.Value;
        Assert.AreEqual(AdapterB, other.Adapter);
        Assert.AreEqual(90d, other.ThreeDPercent);
        Assert.AreEqual(1024UL, other.DedicatedBytes);
    }

    [TestMethod]
    public void ProcessMemoryCanIdentifyAdapterButIsNotReportedAsAdapterVram()
    {
        using FakeGpuCounters source = new();
        using GpuTelemetrySampler sampler = new(source);
        source.Readings = [ProcessMemory(AdapterA, 42, 100), Memory(AdapterA, 512), Memory(AdapterB, 1024)];
        GpuTelemetrySnapshot result = sampler.Sample(42)!.Value;
        Assert.AreEqual(AdapterA, result.Adapter);
        Assert.AreEqual(512UL, result.DedicatedBytes);
        Assert.IsNull(result.ThreeDPercent);
        source.Readings = [Memory(AdapterA, 512), Memory(AdapterB, 1024)];
        Assert.IsNull(sampler.Sample(42));
    }

    [TestMethod]
    public void StopAndDisposeReleaseSourceAndRequireFreshBaseline()
    {
        FakeGpuCounters source = new();
        using (GpuTelemetrySampler sampler = new(source))
        {
            source.Readings = [Engine(AdapterA, 42, 0, 0)];
            _ = sampler.Sample(42);
            sampler.Reset();
            Assert.AreEqual(1, source.Stops);
            source.Advance();
            Assert.IsNull(sampler.Sample(42));
        }

        Assert.IsTrue(source.Disposed);
    }

    private static GpuCounterReading Engine(GpuAdapterId adapter, int pid, long value, long timestamp,
        int engineId = 0, GpuEngineType type = GpuEngineType.ThreeD) =>
        new(new(adapter, pid, engineId, type, GpuCounterKind.Engine), value, timestamp, true);

    private static GpuCounterReading Memory(GpuAdapterId adapter, long bytes) =>
        new(new(adapter, 0, 0, default, GpuCounterKind.AdapterMemory), bytes, 0, true);

    private static GpuCounterReading ProcessMemory(GpuAdapterId adapter, int pid, long bytes) =>
        new(new(adapter, pid, 0, default, GpuCounterKind.ProcessMemory), bytes, 0, true);

    internal sealed class FakeGpuCounters : IGpuCounterSource
    {
        internal GpuCounterReading[] Readings { get; set; } = [];
        internal bool Supported { get; set; } = true;
        internal int Stops { get; private set; }
        internal bool Disposed { get; private set; }
        internal int Reads { get; private set; }

        public bool TryRead(out ReadOnlySpan<GpuCounterReading> readings)
        {
            Reads++;
            readings = Readings;
            return Supported;
        }

        internal void Advance()
        {
            for (int index = 0; index < Readings.Length; index++)
            {
                GpuCounterReading reading = Readings[index];
                if (reading.Identity.Kind == GpuCounterKind.Engine)
                {
                    Readings[index] = reading with
                    {
                        Value = reading.Value * 2,
                        Timestamp = reading.Timestamp * 2,
                    };
                }
            }
        }

        public void Stop() => Stops++;
        public void Dispose() => Disposed = true;
    }
}

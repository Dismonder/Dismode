using System.Text.Json;
using Dismode.Contracts.SystemOptimization;
using Dismode.Core.History;
using Dismode.Core.Performance;
using Dismode.Core.SystemOptimization;

namespace Dismode.UnitTests.History;

[TestClass]
public sealed class FrameTimeStatisticsTests
{
    private static readonly JsonSerializerOptions LegacySerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [TestMethod]
    public void ConstantFramesKeepTheirActualDuration()
    {
        FrameTimeHistogram histogram = new();
        for (int index = 0; index < 2_000; index++)
        {
            histogram.Add(16.67d);
        }

        Assert.AreEqual(60d, histogram.AverageFramesPerSecond, 0.02d);
        Assert.AreEqual(60d, histogram.CalculateSlowestLow(0.01d), 0.02d);
        Assert.AreEqual(60d, histogram.CalculateSlowestLow(0.001d), 0.02d);
        Assert.AreEqual(0, histogram.HitchCount);
    }

    [TestMethod]
    public void SpikeAndThresholdTiesAgreeWithBenchmark()
    {
        double[] frames = [.. Enumerable.Repeat(10d, 1_996), 30d, 50d, 50d, 200d];
        FrameTimeHistogram histogram = new();
        foreach (double frame in frames)
        {
            histogram.Add(frame);
        }

        BenchmarkMetrics metrics = BenchmarkAnalyzer.Analyze(new(
            Guid.NewGuid(), BenchmarkVariant.Baseline, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(90), frames,
            null, null, null, "profile"));
        Array.Sort(frames);

        Assert.AreEqual(metrics.AverageFramesPerSecond, histogram.AverageFramesPerSecond, 1e-9);
        Assert.AreEqual(1000d / 24.5d, histogram.CalculateSlowestLow(0.01d), 1e-9);
        Assert.AreEqual(8d, histogram.CalculateSlowestLow(0.001d), 1e-9);
        Assert.AreEqual(metrics.OnePercentLowFramesPerSecond, histogram.CalculateSlowestLow(0.01d), 1e-9);
        Assert.AreEqual(metrics.PointOnePercentLowFramesPerSecond, histogram.CalculateSlowestLow(0.001d), 1e-9);
        Assert.AreEqual(metrics.P99FrameTimeMilliseconds, histogram.Percentile(0.99d), 1e-9);
        Assert.AreEqual(1, histogram.HitchCount);
        Assert.AreEqual(metrics.HitchRatePercent, histogram.HitchCount * 100d / histogram.Count);
        Assert.AreEqual(metrics.OnePercentLowFramesPerSecond, FrameTimeStatistics.CalculateSlowestLow(frames, 0.01d));
    }

    [TestMethod]
    public void InterpolationTailRoundingAndMedianHitchThresholdAreShared()
    {
        double[] frames = [10d, 20d, 60d, 180d, 180.05d];
        FrameTimeHistogram histogram = new();
        foreach (double frame in frames)
        {
            histogram.Add(frame);
        }

        Assert.AreEqual(180.025d, FrameTimeStatistics.Percentile(frames, 0.875d), 1e-9);
        Assert.AreEqual(FrameTimeStatistics.Percentile(frames, 0.875d), histogram.Percentile(0.875d), 1e-9);
        Assert.AreEqual(1, histogram.HitchCount);
        Assert.AreEqual(1000d / 180.05d, histogram.CalculateSlowestLow(0.01d), 1e-9);
    }

    [TestMethod]
    public void MillionFramesUseFixedStorageAndPreserveLongFrames()
    {
        FrameTimeHistogram histogram = new();
        histogram.Add(16.67d);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 1; index < 1_000_000; index++)
        {
            histogram.Add(index % 1000 == 0 ? 5_000d : 16.67d);
        }

        Assert.IsLessThan(4_096L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreEqual(1_000_000, histogram.Count);
        Assert.AreEqual(5_000d, histogram.MaximumFrameTimeMilliseconds);
        Assert.AreEqual(999, histogram.HitchCount);
    }

    [TestMethod]
    public void JsonReadsLegacyNullsAndRoundTripsNewStatistics()
    {
        SessionFrameRateStatistics legacy = new(10, 100, 10, 100, 10);
        string json = JsonSerializer.Serialize(legacy, LegacySerializerOptions);
        SessionFrameRateStatistics old = JsonSerializer.Deserialize<SessionFrameRateStatistics>(json)!;
        Assert.IsNull(old.OnePercentLowFramesPerSecond);
        Assert.IsNull(old.PointOnePercentLowFramesPerSecond);
        Assert.IsNull(old.HitchCount);

        SessionFrameRateStatistics current = new(10, 100, 10, 50, 20, 50, 50, 0);
        Assert.AreEqual(current, JsonSerializer.Deserialize<SessionFrameRateStatistics>(JsonSerializer.Serialize(current)));
    }
}

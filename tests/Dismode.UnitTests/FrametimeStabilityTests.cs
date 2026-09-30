using Dismode.Core.History;

namespace Dismode.UnitTests;

[TestClass]
public sealed class FrametimeStabilityTests
{
    [TestMethod]
    public void PerfectFrametimesYieldHighStability()
    {
        SessionFrameRateStatistics stats = new(
            sampleCount: 100,
            averageFramesPerSecond: 60.0,
            averageFrameTimeMilliseconds: 16.6,
            minimumFramesPerSecond: 59.0,
            maximumFrameTimeMilliseconds: 17.0);

        Assert.IsTrue(stats.EstimatedFrametimeStabilityPercent >= 95.0);
        Assert.IsTrue(stats.EstimatedFrametimeStabilityPercent <= 100.0);
    }

    [TestMethod]
    public void StutterSpikesYieldLowerStability()
    {
        SessionFrameRateStatistics stats = new(
            sampleCount: 100,
            averageFramesPerSecond: 60.0,
            averageFrameTimeMilliseconds: 16.6,
            minimumFramesPerSecond: 10.0,
            maximumFrameTimeMilliseconds: 100.0); // 100ms stutter spike

        Assert.IsTrue(stats.EstimatedFrametimeStabilityPercent < 20.0);
    }
}

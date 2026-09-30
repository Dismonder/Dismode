using Dismode.Core.History;

namespace Dismode.UnitTests.History;

[TestClass]
public sealed class SessionFrameRateStatisticsTests
{
    [TestMethod]
    public void ConstructorPreservesMeasuredStatistics()
    {
        SessionFrameRateStatistics statistics = new(
            sampleCount: 120,
            averageFramesPerSecond: 144.5d,
            averageFrameTimeMilliseconds: 6.92d,
            minimumFramesPerSecond: 118.2d,
            maximumFrameTimeMilliseconds: 11.4d);

        Assert.AreEqual(120, statistics.SampleCount);
        Assert.AreEqual(144.5d, statistics.AverageFramesPerSecond);
        Assert.AreEqual(6.92d, statistics.AverageFrameTimeMilliseconds);
        Assert.AreEqual(118.2d, statistics.MinimumFramesPerSecond);
        Assert.AreEqual(11.4d, statistics.MaximumFrameTimeMilliseconds);
    }

    [TestMethod]
    public void ConstructorRejectsMissingOrNonFiniteSamples()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionFrameRateStatistics(
                sampleCount: 0,
                averageFramesPerSecond: 144d,
                averageFrameTimeMilliseconds: 6.94d,
                minimumFramesPerSecond: 120d,
                maximumFrameTimeMilliseconds: 10d));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionFrameRateStatistics(
                sampleCount: 1,
                averageFramesPerSecond: double.NaN,
                averageFrameTimeMilliseconds: 6.94d,
                minimumFramesPerSecond: 120d,
                maximumFrameTimeMilliseconds: 10d));
    }

    [TestMethod]
    public void ConstructorRejectsInconsistentRanges()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => new SessionFrameRateStatistics(
                sampleCount: 2,
                averageFramesPerSecond: 100d,
                averageFrameTimeMilliseconds: 10d,
                minimumFramesPerSecond: 110d,
                maximumFrameTimeMilliseconds: 12d));
        Assert.ThrowsExactly<ArgumentException>(
            () => new SessionFrameRateStatistics(
                sampleCount: 2,
                averageFramesPerSecond: 100d,
                averageFrameTimeMilliseconds: 10d,
                minimumFramesPerSecond: 90d,
                maximumFrameTimeMilliseconds: 8d));
    }
}

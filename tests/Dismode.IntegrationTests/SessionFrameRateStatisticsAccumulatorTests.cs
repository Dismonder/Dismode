using Dismode.Core.History;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class SessionFrameRateStatisticsAccumulatorTests
{
    [TestMethod]
    public void EmptyStreamReturnsNull()
    {
        Assert.IsNull(new SessionFrameRateStatisticsAccumulator().Snapshot());
    }

    [TestMethod]
    [DataRow(0.1d)]
    [DataRow(10_000d)]
    public void SupportedBoundariesRemainValidAfterSummation(double frameTime)
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();
        for (int index = 0; index < 10; index++)
        {
            accumulator.Add(frameTime);
        }

        SessionFrameRateStatistics statistics = accumulator.Snapshot()!;
        Assert.AreEqual(1000d / frameTime, statistics.AverageFramesPerSecond, 1e-9);
        Assert.AreEqual(1000d / frameTime, statistics.OnePercentLowFramesPerSecond!.Value, 1e-9);
    }

    [TestMethod]
    public void SessionFrameRateUsesFrameCountDividedByElapsedTime()
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();
        accumulator.Add(8.33d);
        accumulator.Add(16.67d);

        Assert.AreEqual(80d, accumulator.Snapshot()!.AverageFramesPerSecond, 0.001d);
    }

    [TestMethod]
    public void UnavailableOrInvalidSamplesAreNotCounted()
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();

        accumulator.Add(double.NaN);
        accumulator.Add(double.PositiveInfinity);
        accumulator.Add(0d);
        accumulator.Add(-1d);
        accumulator.Add(10_001d);

        Assert.IsNull(accumulator.Snapshot());
    }

    [TestMethod]
    public void MeasuredFramesPreserveSpikeAndLows()
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();
        for (int index = 0; index < 999; index++)
        {
            accumulator.Add(10d);
        }
        accumulator.Add(200d);

        var statistics = accumulator.Snapshot();

        Assert.IsNotNull(statistics);
        Assert.AreEqual(1_000, statistics.SampleCount);
        Assert.AreEqual(1_000_000d / 10_190d, statistics.AverageFramesPerSecond, 0.001d);
        Assert.AreEqual(
            10.19d,
            statistics.AverageFrameTimeMilliseconds,
            0.001d);
        Assert.AreEqual(5d, statistics.MinimumFramesPerSecond, 0.001d);
        Assert.AreEqual(1_000d / 29d, statistics.OnePercentLowFramesPerSecond!.Value, 0.001d);
        Assert.AreEqual(5d, statistics.PointOnePercentLowFramesPerSecond!.Value, 0.001d);
        Assert.AreEqual(1, statistics.HitchCount);
        Assert.AreEqual(
            200d,
            statistics.MaximumFrameTimeMilliseconds,
            0.001d);
    }

    [TestMethod]
    public void SteadyFrameTimesDoNotBreakTheInvariant()
    {
        // Znalezione przypadkiem, przy zupelnie innym tescie: sesja o bardzo
        // stabilnych klatkach wywracala zamykanie wyjatkiem "Maximum frame
        // time cannot be lower than average frame time". Suma trzydziestu
        // jednakowych wartosci podzielona przez trzydziesci wychodzi o jeden
        // bit powyzej tej wartosci, wiec maksimum bylo od sredniej mniejsze —
        // matematycznie niemozliwe, w arytmetyce double jak najbardziej.
        SessionFrameRateStatisticsAccumulator accumulator = new();
        for (int index = 0; index < 30; index++)
        {
            accumulator.Add(1000d / 60);
        }

        SessionFrameRateStatistics? statistics = accumulator.Snapshot();

        Assert.IsNotNull(statistics);
        Assert.IsGreaterThanOrEqualTo(
            statistics.AverageFrameTimeMilliseconds,
            statistics.MaximumFrameTimeMilliseconds);
        Assert.IsLessThanOrEqualTo(
            statistics.AverageFramesPerSecond,
            statistics.MinimumFramesPerSecond);
    }
}

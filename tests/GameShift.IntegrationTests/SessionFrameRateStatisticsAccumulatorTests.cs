using GameShift.Core.History;
using GameShift.Windows.Processes;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class SessionFrameRateStatisticsAccumulatorTests
{
    [TestMethod]
    public void UnavailableOrInvalidSamplesAreNotCounted()
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();

        accumulator.Add(FrameRateSample.WaitingForGame(42));
        accumulator.Add(
            new(
                FrameRateStatus.Measuring,
                FramesPerSecond: double.NaN,
                FrameTimeMilliseconds: 10d,
                ProcessId: 42,
                Message: "invalid"));

        Assert.IsNull(accumulator.Snapshot());
    }

    [TestMethod]
    public void MeasuredSamplesProduceIntervalStatistics()
    {
        SessionFrameRateStatisticsAccumulator accumulator = new();
        accumulator.Add(
            new(
                FrameRateStatus.Measuring,
                FramesPerSecond: 120d,
                FrameTimeMilliseconds: 8.33d,
                ProcessId: 42,
                Message: "measuring"));
        accumulator.Add(
            new(
                FrameRateStatus.Measuring,
                FramesPerSecond: 60d,
                FrameTimeMilliseconds: 16.67d,
                ProcessId: 42,
                Message: "measuring"));

        var statistics = accumulator.Snapshot();

        Assert.IsNotNull(statistics);
        Assert.AreEqual(2, statistics.SampleCount);
        Assert.AreEqual(90d, statistics.AverageFramesPerSecond, 0.001d);
        Assert.AreEqual(
            12.5d,
            statistics.AverageFrameTimeMilliseconds,
            0.001d);
        Assert.AreEqual(60d, statistics.MinimumFramesPerSecond, 0.001d);
        Assert.AreEqual(
            16.67d,
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
            accumulator.Add(
                new(
                    FrameRateStatus.Measuring,
                    FramesPerSecond: 60d,
                    FrameTimeMilliseconds: 1000d / 60,
                    ProcessId: 42,
                    Message: "steady"));
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

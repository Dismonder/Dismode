using GameShift.Contracts.SystemOptimization;
using GameShift.Core.SystemOptimization;

namespace GameShift.UnitTests.SystemOptimization;

[TestClass]
public sealed class BenchmarkAnalyzerTests
{
    [TestMethod]
    public void RawFrametimesProduceCorrectSlowestOneAndPointOnePercentLows()
    {
        double[] frames = Enumerable.Repeat(10d, 2_000).ToArray();
        for (int index = frames.Length - 20; index < frames.Length - 2; index++)
        {
            frames[index] = 25d;
        }

        frames[^2] = 50d;
        frames[^1] = 50d;

        BenchmarkMetrics metrics = BenchmarkAnalyzer.Analyze(
            CreateCapture(BenchmarkVariant.Baseline, frames));

        Assert.IsTrue(metrics.IsValid);
        Assert.AreEqual(98.2801d, metrics.AverageFramesPerSecond, 0.001d);
        Assert.AreEqual(36.3636d, metrics.OnePercentLowFramesPerSecond, 0.001d);
        Assert.AreEqual(20d, metrics.PointOnePercentLowFramesPerSecond, 0.001d);
        Assert.AreEqual(10d, metrics.MedianFrameTimeMilliseconds, 0.001d);
        Assert.AreEqual(50d, metrics.MaximumFrameTimeMilliseconds, 0.001d);
    }

    [TestMethod]
    public void InvalidAndInsufficientCaptureCannotInfluenceAnExperiment()
    {
        double[] frames = Enumerable.Repeat(16.67d, 1_999)
            .Append(double.NaN)
            .Append(double.PositiveInfinity)
            .Append(0d)
            .ToArray();
        BenchmarkCapture capture = CreateCapture(
            BenchmarkVariant.Candidate,
            frames) with
        {
            MeasurementDuration = TimeSpan.FromSeconds(59),
        };

        BenchmarkMetrics metrics = BenchmarkAnalyzer.Analyze(capture);

        Assert.IsFalse(metrics.IsValid);
        StringAssert.Contains(metrics.InvalidReason, "60");
        StringAssert.Contains(metrics.InvalidReason, "2000");
    }

    [TestMethod]
    public void StableFivePercentCandidateWinsFirstPairWithoutAverageOrHitchRegression()
    {
        BenchmarkMetrics baseline = BenchmarkAnalyzer.Analyze(
            CreateCapture(
                BenchmarkVariant.Baseline,
                Enumerable.Repeat(16.67d, 6_000).ToArray()));
        BenchmarkMetrics candidate = BenchmarkAnalyzer.Analyze(
            CreateCapture(
                BenchmarkVariant.Candidate,
                Enumerable.Repeat(15.5d, 6_000).ToArray()));

        BenchmarkDecision decision = BenchmarkDecisionEngine.Decide(
            [baseline],
            [candidate]);

        Assert.AreEqual(BenchmarkVerdict.CandidateWins, decision.Verdict);
        Assert.IsFalse(decision.RequiresAdditionalPair);
        Assert.IsGreaterThanOrEqualTo(5d, decision.PrimaryMetricImprovementPercent);
    }

    [TestMethod]
    public void AmbiguousFirstPairRequiresSecondPairThenFallsBackToBaseline()
    {
        BenchmarkMetrics baseline = BenchmarkAnalyzer.Analyze(
            CreateCapture(
                BenchmarkVariant.Baseline,
                Enumerable.Repeat(16.67d, 6_000).ToArray()));
        BenchmarkMetrics candidate = BenchmarkAnalyzer.Analyze(
            CreateCapture(
                BenchmarkVariant.Candidate,
                Enumerable.Repeat(16.2d, 6_000).ToArray()));

        BenchmarkDecision first = BenchmarkDecisionEngine.Decide(
            [baseline],
            [candidate]);
        BenchmarkDecision second = BenchmarkDecisionEngine.Decide(
            [baseline, baseline],
            [candidate, candidate]);

        Assert.AreEqual(BenchmarkVerdict.Inconclusive, first.Verdict);
        Assert.IsTrue(first.RequiresAdditionalPair);
        Assert.AreEqual(BenchmarkVerdict.KeepBaseline, second.Verdict);
        Assert.IsFalse(second.RequiresAdditionalPair);
    }

    private static BenchmarkCapture CreateCapture(
        BenchmarkVariant variant,
        IReadOnlyList<double> frameTimes) =>
        new(
            Guid.NewGuid(),
            variant,
            DateTimeOffset.UtcNow,
            StabilizationDuration: TimeSpan.FromSeconds(15),
            MeasurementDuration: TimeSpan.FromSeconds(90),
            FrameTimesMilliseconds: frameTimes,
            AverageCpuPercent: 22,
            AverageGpuBusyPercent: 80,
            AverageUsedRamBytes: 8L * 1024 * 1024 * 1024,
            ProfileStateHash: "sha256:baseline");
}

using Dismode.Contracts.SystemOptimization;

namespace Dismode.Core.SystemOptimization;

public static class BenchmarkDecisionEngine
{
    public const double RequiredPrimaryImprovementPercent = 5d;
    public const double MaximumAverageFpsRegressionPercent = 2d;
    public const int MaximumPairs = 2;

    public static BenchmarkDecision Decide(
        IReadOnlyList<BenchmarkMetrics> baselineCaptures,
        IReadOnlyList<BenchmarkMetrics> candidateCaptures)
    {
        ArgumentNullException.ThrowIfNull(baselineCaptures);
        ArgumentNullException.ThrowIfNull(candidateCaptures);

        if (baselineCaptures.Count == 0 || candidateCaptures.Count == 0)
        {
            return BenchmarkDecision.Invalid(
                "Do porównania potrzebny jest baseline i kandydat.");
        }

        if (baselineCaptures.Any(capture => !capture.IsValid)
            || candidateCaptures.Any(capture => !capture.IsValid))
        {
            return BenchmarkDecision.Invalid(
                "Co najmniej jedno przechwycenie nie spełnia wymagań jakości.");
        }

        double baselinePrimary = baselineCaptures.Average(
            capture => capture.OnePercentLowFramesPerSecond);
        double candidatePrimary = candidateCaptures.Average(
            capture => capture.OnePercentLowFramesPerSecond);
        double baselineAverage = baselineCaptures.Average(
            capture => capture.AverageFramesPerSecond);
        double candidateAverage = candidateCaptures.Average(
            capture => capture.AverageFramesPerSecond);
        double baselineHitches = baselineCaptures.Average(
            capture => capture.HitchRatePercent);
        double candidateHitches = candidateCaptures.Average(
            capture => capture.HitchRatePercent);
        double primaryImprovement = PercentageChange(
            baselinePrimary,
            candidatePrimary);
        double averageChange = PercentageChange(
            baselineAverage,
            candidateAverage);
        bool stable = baselineCaptures.All(capture =>
                capture.HasStableFiveSecondBlocks)
            && candidateCaptures.All(capture =>
                capture.HasStableFiveSecondBlocks);
        bool candidateWins =
            primaryImprovement >= RequiredPrimaryImprovementPercent
            && averageChange >= -MaximumAverageFpsRegressionPercent
            && candidateHitches <= baselineHitches
            && stable;

        int completedPairs = Math.Min(
            baselineCaptures.Count,
            candidateCaptures.Count);
        if (candidateWins)
        {
            return new(
                BenchmarkVerdict.CandidateWins,
                RequiresAdditionalPair: false,
                primaryImprovement,
                averageChange,
                baselineHitches,
                candidateHitches,
                completedPairs,
                "Kandydat przekroczył próg 5%, zachował średni FPS i nie zwiększył liczby hitchy.");
        }

        if (completedPairs < MaximumPairs)
        {
            return new(
                BenchmarkVerdict.Inconclusive,
                RequiresAdditionalPair: true,
                primaryImprovement,
                averageChange,
                baselineHitches,
                candidateHitches,
                completedPairs,
                "Pierwsza para jest słabsza lub niejednoznaczna; wymagany jest drugi przebieg obu wariantów.");
        }

        return new(
            BenchmarkVerdict.KeepBaseline,
            RequiresAdditionalPair: false,
            primaryImprovement,
            averageChange,
            baselineHitches,
            candidateHitches,
            completedPairs,
            "Po dwóch parach kandydat nie spełnił pełnej bramy jakości; pozostaje baseline.");
    }

    private static double PercentageChange(double baseline, double candidate) =>
        baseline <= 0d
            ? 0d
            : ((candidate - baseline) / baseline) * 100d;
}

public sealed record BenchmarkDecision(
    BenchmarkVerdict Verdict,
    bool RequiresAdditionalPair,
    double PrimaryMetricImprovementPercent,
    double AverageFramesPerSecondChangePercent,
    double BaselineHitchRatePercent,
    double CandidateHitchRatePercent,
    int CompletedPairs,
    string Explanation)
{
    public static BenchmarkDecision Invalid(string explanation) =>
        new(
            BenchmarkVerdict.Invalid,
            RequiresAdditionalPair: false,
            PrimaryMetricImprovementPercent: 0,
            AverageFramesPerSecondChangePercent: 0,
            BaselineHitchRatePercent: 0,
            CandidateHitchRatePercent: 0,
            CompletedPairs: 0,
            explanation);
}

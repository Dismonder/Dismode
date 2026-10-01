using Dismode.Contracts.SystemOptimization;

namespace Dismode.Core.SystemOptimization;

public static class BenchmarkAnalyzer
{
    public static readonly TimeSpan MinimumStabilizationDuration =
        TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MinimumMeasurementDuration =
        TimeSpan.FromSeconds(60);
    public const int MinimumFrameCount = 2_000;
    private const double MaximumAcceptedFrameTimeMilliseconds = 1_000d;

    public static BenchmarkMetrics Analyze(BenchmarkCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(capture.FrameTimesMilliseconds);

        double[] validFrameTimes = capture.FrameTimesMilliseconds
            .Where(frameTime =>
                double.IsFinite(frameTime)
                && frameTime > 0d
                && frameTime <= MaximumAcceptedFrameTimeMilliseconds)
            .ToArray();

        List<string> invalidReasons = [];
        if (capture.StabilizationDuration < MinimumStabilizationDuration)
        {
            invalidReasons.Add("Wymagane jest co najmniej 15 sekund stabilizacji.");
        }

        if (capture.MeasurementDuration < MinimumMeasurementDuration)
        {
            invalidReasons.Add("Wymagane jest co najmniej 60 sekund pomiaru.");
        }

        if (validFrameTimes.Length < MinimumFrameCount)
        {
            invalidReasons.Add("Wymagane jest co najmniej 2000 prawidłowych klatek.");
        }

        if (string.IsNullOrWhiteSpace(capture.ProfileStateHash))
        {
            invalidReasons.Add("Brakuje odczytanego stanu profilu.");
        }

        if (validFrameTimes.Length == 0)
        {
            return BenchmarkMetrics.Invalid(
                capture.Variant,
                string.Join(' ', invalidReasons.DefaultIfEmpty(
                    "Przechwycenie nie zawiera prawidłowych klatek.")));
        }

        Array.Sort(validFrameTimes);
        double averageFrameTime = validFrameTimes.Average();
        double median = Percentile(validFrameTimes, 0.50d);
        double p95 = Percentile(validFrameTimes, 0.95d);
        double p99 = Percentile(validFrameTimes, 0.99d);
        double onePercentLow = CalculateSlowestLow(validFrameTimes, 0.01d);
        double pointOnePercentLow = CalculateSlowestLow(validFrameTimes, 0.001d);
        double hitchThreshold = Math.Max(50d, median * 3d);
        int hitchCount = validFrameTimes.Count(value => value > hitchThreshold);
        double hitchRate = hitchCount * 100d / validFrameTimes.Length;
        BlockStability stability = CalculateBlockStability(
            capture.FrameTimesMilliseconds);

        return new(
            capture.Variant,
            IsValid: invalidReasons.Count == 0,
            InvalidReason: invalidReasons.Count == 0
                ? null
                : string.Join(' ', invalidReasons),
            ValidFrameCount: validFrameTimes.Length,
            AverageFramesPerSecond: 1000d / averageFrameTime,
            AverageFrameTimeMilliseconds: averageFrameTime,
            MedianFrameTimeMilliseconds: median,
            P95FrameTimeMilliseconds: p95,
            P99FrameTimeMilliseconds: p99,
            OnePercentLowFramesPerSecond: onePercentLow,
            PointOnePercentLowFramesPerSecond: pointOnePercentLow,
            MaximumFrameTimeMilliseconds: validFrameTimes[^1],
            HitchRatePercent: hitchRate,
            FiveSecondBlockVariationPercent: stability.VariationPercent,
            HasStableFiveSecondBlocks: stability.IsStable,
            AverageCpuPercent: capture.AverageCpuPercent,
            AverageGpuBusyPercent: capture.AverageGpuBusyPercent,
            AverageUsedRamBytes: capture.AverageUsedRamBytes);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        double position = percentile * (sortedValues.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = position - lower;
        return sortedValues[lower]
            + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static double CalculateSlowestLow(
        double[] sortedFrameTimes,
        double fraction)
    {
        int sampleCount = Math.Max(
            1,
            (int)Math.Ceiling(sortedFrameTimes.Length * fraction));
        double slowestAverage = sortedFrameTimes
            .AsSpan(sortedFrameTimes.Length - sampleCount, sampleCount)
            .ToArray()
            .Average();
        return 1000d / slowestAverage;
    }

    private static BlockStability CalculateBlockStability(
        IReadOnlyList<double> frameTimes)
    {
        List<double> blockFramesPerSecond = [];
        double elapsedInBlock = 0d;
        int framesInBlock = 0;
        foreach (double frameTime in frameTimes)
        {
            if (!double.IsFinite(frameTime)
                || frameTime <= 0d
                || frameTime > MaximumAcceptedFrameTimeMilliseconds)
            {
                continue;
            }

            elapsedInBlock += frameTime;
            framesInBlock++;
            if (elapsedInBlock < 5_000d)
            {
                continue;
            }

            blockFramesPerSecond.Add(framesInBlock * 1000d / elapsedInBlock);
            elapsedInBlock = 0d;
            framesInBlock = 0;
        }

        if (blockFramesPerSecond.Count < 2)
        {
            return new(double.PositiveInfinity, false);
        }

        double average = blockFramesPerSecond.Average();
        double variance = blockFramesPerSecond.Average(value =>
            Math.Pow(value - average, 2d));
        double coefficient = average <= 0d
            ? double.PositiveInfinity
            : Math.Sqrt(variance) / average * 100d;
        return new(coefficient, coefficient <= 5d);
    }

    private readonly record struct BlockStability(
        double VariationPercent,
        bool IsStable);
}

public sealed record BenchmarkMetrics(
    BenchmarkVariant Variant,
    bool IsValid,
    string? InvalidReason,
    int ValidFrameCount,
    double AverageFramesPerSecond,
    double AverageFrameTimeMilliseconds,
    double MedianFrameTimeMilliseconds,
    double P95FrameTimeMilliseconds,
    double P99FrameTimeMilliseconds,
    double OnePercentLowFramesPerSecond,
    double PointOnePercentLowFramesPerSecond,
    double MaximumFrameTimeMilliseconds,
    double HitchRatePercent,
    double FiveSecondBlockVariationPercent,
    bool HasStableFiveSecondBlocks,
    double? AverageCpuPercent,
    double? AverageGpuBusyPercent,
    long? AverageUsedRamBytes)
{
    public static BenchmarkMetrics Invalid(
        BenchmarkVariant variant,
        string reason) =>
        new(
            variant,
            IsValid: false,
            reason,
            ValidFrameCount: 0,
            AverageFramesPerSecond: 0,
            AverageFrameTimeMilliseconds: 0,
            MedianFrameTimeMilliseconds: 0,
            P95FrameTimeMilliseconds: 0,
            P99FrameTimeMilliseconds: 0,
            OnePercentLowFramesPerSecond: 0,
            PointOnePercentLowFramesPerSecond: 0,
            MaximumFrameTimeMilliseconds: 0,
            HitchRatePercent: 0,
            FiveSecondBlockVariationPercent: double.PositiveInfinity,
            HasStableFiveSecondBlocks: false,
            AverageCpuPercent: null,
            AverageGpuBusyPercent: null,
            AverageUsedRamBytes: null);
}

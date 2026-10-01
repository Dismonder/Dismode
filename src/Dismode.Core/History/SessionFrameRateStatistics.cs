namespace Dismode.Core.History;

public sealed record SessionFrameRateStatistics
{
    public const double MaximumSupportedFramesPerSecond = 10_000d;
    public const double MaximumSupportedFrameTimeMilliseconds = 10_000d;

    public SessionFrameRateStatistics(
        int sampleCount,
        double averageFramesPerSecond,
        double averageFrameTimeMilliseconds,
        double minimumFramesPerSecond,
        double maximumFrameTimeMilliseconds)
    {
        if (sampleCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount),
                sampleCount,
                "Frame-rate statistics require at least one measured sample.");
        }

        ValidateMetric(
            averageFramesPerSecond,
            MaximumSupportedFramesPerSecond,
            nameof(averageFramesPerSecond));
        ValidateMetric(
            averageFrameTimeMilliseconds,
            MaximumSupportedFrameTimeMilliseconds,
            nameof(averageFrameTimeMilliseconds));
        ValidateMetric(
            minimumFramesPerSecond,
            MaximumSupportedFramesPerSecond,
            nameof(minimumFramesPerSecond));
        ValidateMetric(
            maximumFrameTimeMilliseconds,
            MaximumSupportedFrameTimeMilliseconds,
            nameof(maximumFrameTimeMilliseconds));
        if (minimumFramesPerSecond > averageFramesPerSecond)
        {
            throw new ArgumentException(
                "Minimum FPS cannot exceed average FPS.",
                nameof(minimumFramesPerSecond));
        }

        if (maximumFrameTimeMilliseconds < averageFrameTimeMilliseconds)
        {
            throw new ArgumentException(
                "Maximum frame time cannot be lower than average frame time.",
                nameof(maximumFrameTimeMilliseconds));
        }

        SampleCount = sampleCount;
        AverageFramesPerSecond = averageFramesPerSecond;
        AverageFrameTimeMilliseconds = averageFrameTimeMilliseconds;
        MinimumFramesPerSecond = minimumFramesPerSecond;
        MaximumFrameTimeMilliseconds = maximumFrameTimeMilliseconds;
    }

    public int SampleCount { get; }

    public double AverageFramesPerSecond { get; }

    public double AverageFrameTimeMilliseconds { get; }

    public double MinimumFramesPerSecond { get; }

    public double MaximumFrameTimeMilliseconds { get; }

    public double EstimatedFrametimeStabilityPercent =>
        AverageFrameTimeMilliseconds > 0 && MaximumFrameTimeMilliseconds > 0
            ? Math.Clamp((AverageFrameTimeMilliseconds / MaximumFrameTimeMilliseconds) * 100d, 0d, 100d)
            : 100d;

    private static void ValidateMetric(
        double value,
        double maximum,
        string parameterName)
    {
        if (!double.IsFinite(value) || value is <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The measured metric must be finite and between 0 and "
                + $"{maximum}.");
        }
    }
}

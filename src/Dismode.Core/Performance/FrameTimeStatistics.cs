namespace Dismode.Core.Performance;

public static class FrameTimeStatistics
{
    public static double Percentile(double[] sortedValues, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);
        return Percentile(sortedValues.Length, index => sortedValues[index], percentile);
    }

    internal static double Percentile(
        int count,
        Func<int, double> valueAt,
        double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (!double.IsFinite(percentile) || percentile is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        double position = percentile * (count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        double lowerValue = valueAt(lower);
        return lower == upper
            ? lowerValue
            : lowerValue + ((valueAt(upper) - lowerValue) * (position - lower));
    }

    public static double CalculateSlowestLow(double[] sortedFrameTimes, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sortedFrameTimes);
        return CalculateSlowestLow(
            sortedFrameTimes.Length,
            sampleCount => sortedFrameTimes
                .AsSpan(sortedFrameTimes.Length - sampleCount, sampleCount)
                .ToArray()
                .Average(),
            fraction);
    }

    internal static double CalculateSlowestLow(
        int count,
        Func<int, double> slowestAverage,
        double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (!double.IsFinite(fraction) || fraction is <= 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        int sampleCount = Math.Max(1, (int)Math.Ceiling(count * fraction));
        return 1000d / slowestAverage(sampleCount);
    }

    public static double HitchThreshold(double medianFrameTimeMilliseconds) =>
        Math.Max(50d, medianFrameTimeMilliseconds * 3d);
}

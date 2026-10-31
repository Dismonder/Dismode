using Dismode.Core.History;

namespace Dismode.Core.Performance;

public sealed class FrameTimeHistogram
{
    public const double BucketWidthMilliseconds = 0.05d;
    private const int BucketCount = (int)(
        SessionFrameRateStatistics.MaximumSupportedFrameTimeMilliseconds
        / BucketWidthMilliseconds) + 1;
    private readonly int[] _counts = new int[BucketCount];
    private readonly double[] _totals = new double[BucketCount];
    private double _totalMilliseconds;

    public int Count { get; private set; }

    public double AverageFramesPerSecond => Math.Min(
        SessionFrameRateStatistics.MaximumSupportedFramesPerSecond,
        Count * 1000d / _totalMilliseconds);

    public double AverageFrameTimeMilliseconds => _totalMilliseconds / Count;

    public double MaximumFrameTimeMilliseconds { get; private set; }

    public int HitchCount
    {
        get
        {
            // Prog jest wspolny z benchmarkiem; mediana i porownanie koszyka
            // maja te sama rozdzielczosc 0.05 ms co pozostale kwantyle.
            double threshold = FrameTimeStatistics.HitchThreshold(Percentile(0.5d));
            int count = 0;
            for (int index = 0; index < BucketCount; index++)
            {
                if (_counts[index] > 0 && _totals[index] / _counts[index] > threshold)
                {
                    count += _counts[index];
                }
            }

            return count;
        }
    }

    public void Add(double frameTimeMilliseconds)
    {
        if (!double.IsFinite(frameTimeMilliseconds)
            || frameTimeMilliseconds < 1000d / SessionFrameRateStatistics.MaximumSupportedFramesPerSecond
            || frameTimeMilliseconds > SessionFrameRateStatistics.MaximumSupportedFrameTimeMilliseconds
            || Count == int.MaxValue)
        {
            return;
        }

        int bucket = (int)(frameTimeMilliseconds / BucketWidthMilliseconds);
        _counts[bucket]++;
        _totals[bucket] += frameTimeMilliseconds;
        _totalMilliseconds += frameTimeMilliseconds;
        Count++;
        MaximumFrameTimeMilliseconds = Math.Max(MaximumFrameTimeMilliseconds, frameTimeMilliseconds);
    }

    public void Merge(FrameTimeHistogram other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if ((long)Count + other.Count > int.MaxValue)
        {
            return;
        }

        for (int index = 0; index < BucketCount; index++)
        {
            _counts[index] += other._counts[index];
            _totals[index] += other._totals[index];
        }

        Count += other.Count;
        _totalMilliseconds += other._totalMilliseconds;
        MaximumFrameTimeMilliseconds = Math.Max(MaximumFrameTimeMilliseconds, other.MaximumFrameTimeMilliseconds);
    }

    public double Percentile(double percentile) =>
        FrameTimeStatistics.Percentile(Count, ValueAt, percentile);

    public double CalculateSlowestLow(double fraction) => Math.Min(
        // Suma czasow na granicy 10000 FPS moze zaokraglic sie w dol.
        SessionFrameRateStatistics.MaximumSupportedFramesPerSecond,
        FrameTimeStatistics.CalculateSlowestLow(Count, SlowestAverage, fraction));

    private double SlowestAverage(int sampleCount)
    {
        int remaining = sampleCount;
        double total = 0d;
        // Srednia koszyka zachowuje stale czasy (np. 16.67 ms). Przy podziale
        // koszyka blad czasu klatki nie przekracza 0.05 ms. Nie trzymamy klatek.
        for (int index = BucketCount - 1; index >= 0 && remaining > 0; index--)
        {
            int take = Math.Min(remaining, _counts[index]);
            if (take > 0)
            {
                total += take == _counts[index]
                    ? _totals[index]
                    : _totals[index] / _counts[index] * take;
                remaining -= take;
            }
        }

        return total / sampleCount;
    }

    private double ValueAt(int position)
    {
        for (int index = 0; index < BucketCount; index++)
        {
            if (position < _counts[index])
            {
                return _totals[index] / _counts[index];
            }

            position -= _counts[index];
        }

        throw new ArgumentOutOfRangeException(nameof(position));
    }
}

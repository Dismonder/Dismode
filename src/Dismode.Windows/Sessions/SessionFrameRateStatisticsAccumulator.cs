using Dismode.Core.History;
using Dismode.Core.Performance;

namespace Dismode.Windows.Sessions;

internal sealed class SessionFrameRateStatisticsAccumulator
{
    private readonly object _sync = new();
    private readonly FrameTimeHistogram _histogram = new();

    internal void Add(double frameTimeMilliseconds)
    {
        lock (_sync)
        {
            _histogram.Add(frameTimeMilliseconds);
        }
    }

    internal void Merge(FrameTimeHistogram histogram)
    {
        lock (_sync)
        {
            _histogram.Merge(histogram);
        }
    }

    internal SessionFrameRateStatistics? Snapshot()
    {
        lock (_sync)
        {
            if (_histogram.Count == 0)
            {
                return null;
            }

            double averageFrameTime = _histogram.AverageFrameTimeMilliseconds;
            double averageFramesPerSecond = _histogram.AverageFramesPerSecond;
            double maximumFrameTime = _histogram.MaximumFrameTimeMilliseconds;

            // Maksimum nie moze byc mniejsze od sredniej, a minimum wieksze —
            // matematycznie. W arytmetyce zmiennoprzecinkowej moze: suma n
            // jednakowych wartosci podzielona przez n bywa o jeden bit obok
            // tej wartosci, i przy stabilnych klatkach dokladnie tak wychodzi.
            // Konstruktor slusznie odrzuca odwrocone dane, wiec to producent
            // ma oddac liczby spojne, zamiast wywracac zamykanie sesji na
            // bledzie reprezentacji.
            return new(
                _histogram.Count,
                averageFramesPerSecond,
                averageFrameTime,
                Math.Min(1000d / maximumFrameTime, averageFramesPerSecond),
                Math.Max(maximumFrameTime, averageFrameTime),
                _histogram.CalculateSlowestLow(0.01d),
                _histogram.CalculateSlowestLow(0.001d),
                _histogram.HitchCount);
        }
    }
}

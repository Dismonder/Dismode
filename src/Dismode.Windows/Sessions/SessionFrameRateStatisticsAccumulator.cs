using Dismode.Core.History;
using Dismode.Windows.Processes;

namespace Dismode.Windows.Sessions;

internal sealed class SessionFrameRateStatisticsAccumulator
{
    private readonly object _sync = new();
    private int _sampleCount;
    private double _framesPerSecondTotal;
    private double _frameTimeTotal;
    private double _minimumFramesPerSecond = double.PositiveInfinity;
    private double _maximumFrameTimeMilliseconds;

    internal void Add(FrameRateSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Status != FrameRateStatus.Measuring
            || sample.FramesPerSecond is not double framesPerSecond
            || sample.FrameTimeMilliseconds is not double frameTime
            || !IsValidMetric(
                framesPerSecond,
                SessionFrameRateStatistics.MaximumSupportedFramesPerSecond)
            || !IsValidMetric(
                frameTime,
                SessionFrameRateStatistics
                    .MaximumSupportedFrameTimeMilliseconds))
        {
            return;
        }

        lock (_sync)
        {
            if (_sampleCount == int.MaxValue)
            {
                return;
            }

            _sampleCount++;
            _framesPerSecondTotal += framesPerSecond;
            _frameTimeTotal += frameTime;
            _minimumFramesPerSecond = Math.Min(
                _minimumFramesPerSecond,
                framesPerSecond);
            _maximumFrameTimeMilliseconds = Math.Max(
                _maximumFrameTimeMilliseconds,
                frameTime);
        }
    }

    internal SessionFrameRateStatistics? Snapshot()
    {
        lock (_sync)
        {
            if (_sampleCount == 0)
            {
                return null;
            }

            double averageFrameTime = _frameTimeTotal / _sampleCount;
            double averageFramesPerSecond =
                _framesPerSecondTotal / _sampleCount;

            // Maksimum nie moze byc mniejsze od sredniej, a minimum wieksze —
            // matematycznie. W arytmetyce zmiennoprzecinkowej moze: suma n
            // jednakowych wartosci podzielona przez n bywa o jeden bit obok
            // tej wartosci, i przy stabilnych klatkach dokladnie tak wychodzi.
            // Konstruktor slusznie odrzuca odwrocone dane, wiec to producent
            // ma oddac liczby spojne, zamiast wywracac zamykanie sesji na
            // bledzie reprezentacji.
            return new(
                _sampleCount,
                averageFramesPerSecond,
                averageFrameTime,
                Math.Min(_minimumFramesPerSecond, averageFramesPerSecond),
                Math.Max(_maximumFrameTimeMilliseconds, averageFrameTime));
        }
    }

    private static bool IsValidMetric(double value, double maximum) =>
        double.IsFinite(value) && value is > 0 && value <= maximum;
}

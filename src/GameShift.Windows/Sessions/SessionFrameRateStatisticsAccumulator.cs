using GameShift.Core.History;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

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
            return _sampleCount == 0
                ? null
                : new(
                    _sampleCount,
                    _framesPerSecondTotal / _sampleCount,
                    _frameTimeTotal / _sampleCount,
                    _minimumFramesPerSecond,
                    _maximumFrameTimeMilliseconds);
        }
    }

    private static bool IsValidMetric(double value, double maximum) =>
        double.IsFinite(value) && value is > 0 && value <= maximum;
}

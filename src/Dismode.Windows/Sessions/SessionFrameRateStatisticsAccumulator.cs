using Dismode.Core.History;
using Dismode.Core.Performance;

namespace Dismode.Windows.Sessions;

internal sealed class SessionFrameRateStatisticsAccumulator
{
    private readonly object _sync = new();
    private readonly FrameTimeHistogram _histogram = new();
    // Tworzony przy pierwszej wyswietlonej klatce: przy wylaczonym sledzeniu
    // wyswietlania nie kosztuje nic (histogram to ~2,4 MB).
    private FrameTimeHistogram? _displayedHistogram;
    // Low z wyswietlonych klatek tylko z sensownej proby; poprzeczka z nakladki
    // (osiem klatek), bo sesyjne lows nie maja dotad zadnego progu.
    internal const int MinimumDisplayedLowSamples = 8;
    private int _droppedFrames;
    private bool _displayTracked;

    internal void Add(
        double frameTimeMilliseconds,
        double? displayedTimeMilliseconds = null,
        bool dropped = false)
    {
        lock (_sync)
        {
            _histogram.Add(frameTimeMilliseconds);
            if (displayedTimeMilliseconds is double displayed)
            {
                _displayTracked = true;
                (_displayedHistogram ??= new()).Add(displayed);
            }
            else if (dropped)
            {
                _displayTracked = true;
                if (_droppedFrames < int.MaxValue)
                {
                    _droppedFrames++;
                }
            }
        }
    }

    internal void Merge(
        FrameTimeHistogram histogram,
        FrameTimeHistogram? displayedHistogram = null,
        int droppedFrames = 0)
    {
        lock (_sync)
        {
            _histogram.Merge(histogram);
            if (displayedHistogram is { Count: > 0 } || droppedFrames > 0)
            {
                _displayTracked = true;
                if (displayedHistogram is not null)
                {
                    (_displayedHistogram ??= new()).Merge(displayedHistogram);
                }

                _droppedFrames = (int)Math.Min(
                    (long)_droppedFrames + droppedFrames,
                    int.MaxValue);
            }
        }
    }

    /// <summary>
    /// Wyswietlone FPS i zgubione klatki sesji; null, gdy sledzenie
    /// wyswietlania nie dalo zadnej klatki (ustawienie wylaczone albo brak
    /// kolumny DisplayedTime). Nie jest zapisywane w historii.
    /// </summary>
    internal SessionDisplayedFrameStatistics? SnapshotDisplayed()
    {
        lock (_sync)
        {
            return _displayTracked && _displayedHistogram is { Count: > 0 }
                ? new(
                    _displayedHistogram.Count,
                    _displayedHistogram.AverageFramesPerSecond,
                    _droppedFrames)
                : null;
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

            // Low z samych wyswietlonych klatek, gdy jest ich dosc: co najmniej
            // MinimumDisplayedLowSamples i polowa klatek prezentacji. Inaczej
            // z klatek prezentacji, jak dotychczas.
            FrameTimeHistogram lowSource = _displayTracked
                && _displayedHistogram is { } displayedSource
                && displayedSource.Count >= MinimumDisplayedLowSamples
                && displayedSource.Count * 2L >= _histogram.Count
                    ? displayedSource
                    : _histogram;

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
                lowSource.CalculateSlowestLow(0.01d),
                lowSource.CalculateSlowestLow(0.001d),
                _histogram.HitchCount);
        }
    }
}

internal sealed record SessionDisplayedFrameStatistics(
    int DisplayedFrameCount,
    double DisplayedFramesPerSecond,
    int DroppedFrames);

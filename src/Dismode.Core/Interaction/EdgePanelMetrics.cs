using Dismode.Core.Profiles;

namespace Dismode.Core.Interaction;

public readonly record struct EdgePanelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) =>
        Width > 0 && Height > 0 && x >= X && x < Right && y >= Y && y < Bottom;

    public EdgePanelRect Inflate(int margin) =>
        new(
            X - margin,
            Y - margin,
            Math.Max(0, Width + (margin * 2)),
            Math.Max(0, Height + (margin * 2)));
}

public sealed class EdgePanelMetrics(TimeProvider clock)
{
    public const int Capacity = 240;
    public const int HotStripWidthPixels = 2;
    public const double HotStripTopRatio = 0.20;
    public const double HotStripBottomRatio = 0.80;
    public const double LeaveMarginDip = 32;

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly (long Timestamp, double Fps)[] _samples = new (long, double)[Capacity];
    private int _head;
    private int _count;

    public void Add(double? fps)
    {
        if (fps is not double value || !double.IsFinite(value) || value < 0)
        {
            return;
        }

        _samples[_head] = (_clock.GetTimestamp(), value);
        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
    }

    public void Clear() => _count = 0;

    public IReadOnlyList<(double AgeSeconds, double Fps)> Read()
    {
        List<(double, double)> result = new(_count);
        for (int i = 0; i < _count; i++)
        {
            (long timestamp, double fps) = _samples[(_head - _count + i + Capacity) % Capacity];
            double age = _clock.GetElapsedTime(timestamp).TotalSeconds;
            if (age <= 60)
            {
                result.Add((age, fps));
            }
        }

        return result;
    }

    public static EdgePanelRect GetHotStripBounds(EdgePanelRect monitorBounds)
    {
        if (monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
        {
            return default;
        }

        int width = Math.Min(HotStripWidthPixels, monitorBounds.Width);
        int top = monitorBounds.Y + (int)Math.Round(monitorBounds.Height * HotStripTopRatio);
        int bottom = monitorBounds.Y + (int)Math.Round(monitorBounds.Height * HotStripBottomRatio);
        return new(monitorBounds.Right - width, top, width, Math.Max(0, bottom - top));
    }

    public static bool IsInHotZone(int x, int y, EdgePanelRect monitorBounds, EdgePanelRect handleBounds) =>
        handleBounds.Contains(x, y) || GetHotStripBounds(monitorBounds).Contains(x, y);

    public static EdgePanelRect GetExpandedCardBounds(EdgePanelRect cardBounds, double scale) =>
        cardBounds.Inflate((int)Math.Ceiling(LeaveMarginDip * Math.Max(1, scale)));

    public static bool IsInOpenPanelZone(
        int x,
        int y,
        EdgePanelRect cardBounds,
        EdgePanelRect monitorBounds,
        EdgePanelRect handleBounds,
        double scale,
        bool pointerEntered) =>
        (pointerEntered ? GetExpandedCardBounds(cardBounds, scale) : cardBounds).Contains(x, y)
        || IsInHotZone(x, y, monitorBounds, handleBounds);

    public static int GetOverlaySegment(bool enabled, PerformanceOverlayStyle style) => !enabled ? 0 : style switch
    {
        PerformanceOverlayStyle.MinimalText => 1,
        PerformanceOverlayStyle.FpsAndLows => 2,
        PerformanceOverlayStyle.CompactBar => 3,
        _ => 4,
    };

    public static PerformanceOverlayStyle? GetOverlayStyle(int segment) => segment switch
    {
        1 => PerformanceOverlayStyle.MinimalText,
        2 => PerformanceOverlayStyle.FpsAndLows,
        3 => PerformanceOverlayStyle.CompactBar,
        4 => PerformanceOverlayStyle.FullDeck,
        _ => null,
    };
}

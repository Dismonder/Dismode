namespace GameShift.Core.Profiles;

public enum PerformanceOverlayCorner
{
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 3,
    BottomRight = 4,
}

public enum PerformanceOverlayStyle
{
    FullDeck = 1,
    CompactBar = 2,
    MinimalText = 3,
}

public enum PerformanceOverlayTheme
{
    CyberNeon = 1,
    MatrixGreen = 2,
    ToxicGreen = 3,
    ApexAmber = 4,
    CrimsonRed = 5,
    PureWhite = 6,
    StealthPurple = 7,
}

public sealed record PerformanceOverlayPreferences
{
    public const int MinimumOpacityPercent = 20;
    public const int MaximumOpacityPercent = 100;
    public const int MinimumScalePercent = 75;
    public const int MaximumScalePercent = 150;
    public const int DefaultOpacityPercent = 94;
    public const int DefaultScalePercent = 100;

    public PerformanceOverlayPreferences(
        bool isEnabled,
        int opacityPercent,
        int scalePercent,
        PerformanceOverlayCorner corner,
        PerformanceOverlayStyle style,
        PerformanceOverlayTheme theme,
        DateTimeOffset updatedAtUtc)
    {
        if (opacityPercent is
            < MinimumOpacityPercent
            or > MaximumOpacityPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(opacityPercent),
                opacityPercent,
                $"Overlay opacity must be between "
                + $"{MinimumOpacityPercent} and "
                + $"{MaximumOpacityPercent} percent.");
        }

        if (scalePercent is
            < MinimumScalePercent
            or > MaximumScalePercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scalePercent),
                scalePercent,
                $"Overlay scale must be between "
                + $"{MinimumScalePercent} and "
                + $"{MaximumScalePercent} percent.");
        }

        if (!Enum.IsDefined(corner))
        {
            throw new ArgumentOutOfRangeException(
                nameof(corner),
                corner,
                "The overlay corner is not recognized.");
        }

        if (!Enum.IsDefined(style))
        {
            throw new ArgumentOutOfRangeException(
                nameof(style),
                style,
                "The overlay style is not recognized.");
        }

        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(
                nameof(theme),
                theme,
                "The overlay theme is not recognized.");
        }

        IsEnabled = isEnabled;
        OpacityPercent = opacityPercent;
        ScalePercent = scalePercent;
        Corner = corner;
        Style = style;
        Theme = theme;
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }

    public PerformanceOverlayPreferences(
        bool isEnabled,
        int opacityPercent,
        int scalePercent,
        PerformanceOverlayCorner corner,
        DateTimeOffset updatedAtUtc)
        : this(
            isEnabled,
            opacityPercent,
            scalePercent,
            corner,
            PerformanceOverlayStyle.FullDeck,
            PerformanceOverlayTheme.CyberNeon,
            updatedAtUtc)
    {
    }

    public bool IsEnabled { get; }

    public int OpacityPercent { get; }

    public int ScalePercent { get; }

    public PerformanceOverlayCorner Corner { get; }

    public PerformanceOverlayStyle Style { get; }

    public PerformanceOverlayTheme Theme { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static PerformanceOverlayPreferences CreateDefault() =>
        new(
            isEnabled: true,
            DefaultOpacityPercent,
            DefaultScalePercent,
            PerformanceOverlayCorner.TopRight,
            PerformanceOverlayStyle.FullDeck,
            PerformanceOverlayTheme.CyberNeon,
            DateTimeOffset.UtcNow);
}


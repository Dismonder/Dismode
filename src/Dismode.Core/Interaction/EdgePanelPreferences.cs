namespace Dismode.Core.Interaction;

public sealed record EdgePanelPreferences
{
    // Srednia alfa dawnego uchwytu: akcentowe linie na tle #2A00D7E2.
    public const int DefaultHandleTransparencyPercent = 50;

    public EdgePanelPreferences(bool isEnabled = true, int handleTransparencyPercent = DefaultHandleTransparencyPercent)
    {
        IsEnabled = isEnabled;
        HandleTransparencyPercent = Math.Clamp(handleTransparencyPercent, 0, 90);
    }

    public bool IsEnabled { get; }
    public int HandleTransparencyPercent { get; }
    public double HandleOpacity => 1 - (HandleTransparencyPercent / 100d);
}

public interface IEdgePanelPreferencesRepository
{
    ValueTask<EdgePanelPreferences> LoadEdgePanelPreferencesAsync(CancellationToken cancellationToken);
    ValueTask SaveEdgePanelPreferencesAsync(EdgePanelPreferences preferences, CancellationToken cancellationToken);
}

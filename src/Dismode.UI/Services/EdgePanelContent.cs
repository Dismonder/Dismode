using Dismode.Core.History;
using Dismode.Core.Profiles;

namespace Dismode.UI.Services;

internal sealed record EdgePanelContent(
    string? GameName,
    int? ProcessId,
    double? FramesPerSecond,
    bool SessionActive,
    bool CanToggleSession,
    string? SelectedGameName,
    bool OverlayEnabled,
    PerformanceOverlayStyle OverlayStyle,
    bool VendorTelemetryEnabled,
    SessionSummary? LastSession);

internal interface IEdgePanelActions
{
    EdgePanelContent ReadEdgePanelContent();
    Task ToggleEdgePanelSessionAsync(Microsoft.UI.Xaml.XamlRoot dialogRoot);
    void SetEdgePanelOverlay(bool enabled);
    void SetEdgePanelOverlayStyle(PerformanceOverlayStyle style);
}

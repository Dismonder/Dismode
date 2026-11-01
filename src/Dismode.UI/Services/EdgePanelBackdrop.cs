using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dismode.UI.Services;

// Panel nie przejmuje fokusu gry; akryl pozostaje aktywny takze bez aktywacji okna.
internal sealed class EdgePanelBackdrop : SystemBackdrop, IDisposable
{
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        UpdateConfiguration(connectedTarget, xamlRoot);
        _controller = new();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        UpdateConfiguration(target, xamlRoot);
    }

    private void UpdateConfiguration(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        SystemBackdropConfiguration defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _configuration.Theme = defaults.Theme;
        _configuration.IsHighContrast = defaults.IsHighContrast;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        Dispose();
        base.OnTargetDisconnected(disconnectedTarget);
    }

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
    }
}

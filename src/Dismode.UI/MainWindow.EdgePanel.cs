using System.Diagnostics;
using Dismode.Core.Profiles;
using Dismode.UI.Services;
using Dismode.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dismode.UI;

public sealed partial class MainWindow : IEdgePanelActions
{
    private EdgePanelWindow? _edgePanel;
    private PerformanceOverlayStyle _edgeOverlayStyle = PerformanceOverlayStyle.FullDeck;
    private ForegroundGameSample? _edgeForegroundSample;
    private bool _loadingEdgePreferences;
    private readonly SemaphoreSlim _edgePreferenceSaveGate = new(1, 1);

    private async Task LoadEdgePanelPreferencesAsync(CancellationToken cancellationToken)
    {
        _loadingEdgePreferences = true;
        try
        {
            EdgePanelSettingsToggleSwitch.IsOn = await _userDataStore.LoadEdgePanelEnabledAsync(cancellationToken);
            ApplyEdgePanelEnabled();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            Debug.WriteLine(exception);
            ShowInfo(PlanInfoBar, InfoBarSeverity.Warning, "Panel jest niedostępny", "Spróbuj ponownie uruchomić Dismode.");
        }
        finally
        {
            _loadingEdgePreferences = false;
        }
    }

    private void ApplyEdgePanelEnabled()
    {
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return;
        }

        if (EdgePanelSettingsToggleSwitch.IsOn)
        {
            _edgePanel ??= new(this);
        }
        else
        {
            _edgePanel?.Dispose();
            _edgePanel = null;
        }
    }

    private async void OnEdgePanelSettingChanged(object sender, RoutedEventArgs args)
    {
        if (!_isLoaded || _loadingEdgePreferences || _disposed)
        {
            return;
        }

        TacticalAudioService.Instance.PlayToggle();
        try
        {
            ApplyEdgePanelEnabled();
            await _edgePreferenceSaveGate.WaitAsync(_lifetime.Token);
            try
            {
                await _userDataStore.SaveEdgePanelEnabledAsync(EdgePanelSettingsToggleSwitch.IsOn, _lifetime.Token);
            }
            finally
            {
                _edgePreferenceSaveGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            Debug.WriteLine(exception);
            ShowInfo(PlanInfoBar, InfoBarSeverity.Warning, "Nie zapisano ustawienia panelu", "Spróbuj ponownie uruchomić Dismode.");
        }
    }

    EdgePanelContent IEdgePanelActions.ReadEdgePanelContent()
    {
        ProfileListItem? selected = ProfilesList.SelectedItem as ProfileListItem;
        SessionStateClientSnapshot? session = _activeSession;
        ForegroundGameSample? foreground = FpsTrackingToggleSwitch.IsOn && _overlayPollTimer.IsEnabled
            ? _edgeForegroundSample : null;
        return new(
            session?.GameDisplayName ?? foreground?.ProcessName,
            session?.FrameRateProcessId ?? foreground?.ProcessId,
            FpsTrackingToggleSwitch.IsOn ? session?.FramesPerSecond ?? foreground?.FramesPerSecond : null,
            session is not null,
            !_isBusy && (session is not null || selected is not null),
            selected?.DisplayName,
            FpsOverlayToggleSwitch.IsOn,
            _edgeOverlayStyle,
            VendorTelemetrySettingsToggleSwitch.IsOn,
            _history.FirstOrDefault()?.Summary);
    }

    async Task IEdgePanelActions.ToggleEdgePanelSessionAsync(XamlRoot dialogRoot)
    {
        if (_isBusy || _disposed)
        {
            return;
        }

        if (_activeSession is not null)
        {
            await RestoreActiveSessionAsync(dialogRoot);
        }
        else if (ProfilesList.SelectedItem is ProfileListItem selected)
        {
            await LaunchProfileThroughDismodeAsync(selected, keepWindowHidden: false, _lifetime.Token);
        }
    }

    void IEdgePanelActions.SetEdgePanelOverlay(bool enabled)
    {
        SetFpsOverlayToggleValues(enabled);
        ApplyOverlaySettingsFromControls(scheduleSave: true);
    }

    void IEdgePanelActions.SetEdgePanelOverlayStyle(PerformanceOverlayStyle style)
    {
        _edgeOverlayStyle = style;
        ApplyOverlaySettingsFromControls(scheduleSave: true);
    }
}

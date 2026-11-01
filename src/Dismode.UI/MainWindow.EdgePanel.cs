using System.Diagnostics;
using Dismode.Core.Interaction;
using Dismode.Core.Profiles;
using Dismode.UI.Services;
using Dismode.UI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Dismode.UI;

public sealed partial class MainWindow : IEdgePanelActions
{
    private EdgePanelWindow? _edgePanel;
    private ForegroundGameSample? _edgeForegroundSample;
    private bool _loadingEdgePreferences;
    private readonly SemaphoreSlim _edgePreferenceSaveGate = new(1, 1);
    private readonly EdgePanelMetrics _edgeFpsHistory = new(TimeProvider.System);
    private CancellationTokenSource? _edgeTransparencySave;
    private int? _edgeFpsProcessId;

    private async Task LoadEdgePanelPreferencesAsync(CancellationToken cancellationToken)
    {
        _loadingEdgePreferences = true;
        try
        {
            EdgePanelPreferences preferences = await _userDataStore.LoadEdgePanelPreferencesAsync(cancellationToken);
            EdgePanelSettingsToggleSwitch.IsOn = preferences.IsEnabled;
            EdgeHandleTransparencySlider.Value = preferences.HandleTransparencyPercent;
            EdgeHandleTransparencyValue.Text = $"{preferences.HandleTransparencyPercent} %";
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
            _edgePanel.SetHandleTransparency((int)EdgeHandleTransparencySlider.Value);
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

    private async void OnEdgeHandleTransparencyChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        int percent = (int)(Math.Round(args.NewValue / 5) * 5);
        if (EdgeHandleTransparencyValue is not null)
        {
            EdgeHandleTransparencyValue.Text = $"{percent} %";
        }
        _edgePanel?.SetHandleTransparency(percent);
        if (!_isLoaded || _loadingEdgePreferences || _disposed)
        {
            return;
        }

        _edgeTransparencySave?.Cancel();
        using CancellationTokenSource pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _edgeTransparencySave = pending;
        try
        {
            await Task.Delay(300, pending.Token);
            await _edgePreferenceSaveGate.WaitAsync(pending.Token);
            try
            {
                await _userDataStore.SaveEdgePanelPreferencesAsync(
                    new(EdgePanelSettingsToggleSwitch.IsOn, percent), pending.Token);
            }
            finally
            {
                _edgePreferenceSaveGate.Release();
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            Debug.WriteLine(exception);
            ShowInfo(PlanInfoBar, InfoBarSeverity.Warning, "Nie zapisano ustawienia panelu", "Spróbuj ponownie uruchomić Dismode.");
        }
        finally
        {
            if (ReferenceEquals(_edgeTransparencySave, pending))
            {
                _edgeTransparencySave = null;
            }
        }
    }

    private void RecordEdgePanelFps(int? processId, double? fps)
    {
        if (_edgeFpsProcessId != processId)
        {
            _edgeFpsHistory.Clear();
            _edgeFpsProcessId = processId;
        }
        if (FpsTrackingToggleSwitch.IsOn)
        {
            _edgeFpsHistory.Add(fps);
        }
        else
        {
            _edgeFpsHistory.Clear();
        }
    }

    EdgePanelContent IEdgePanelActions.ReadEdgePanelContent()
    {
        ProfileListItem? selected = ProfilesList.SelectedItem as ProfileListItem;
        SessionStateClientSnapshot? session = _activeSession;
        ForegroundGameSample? foreground = FpsTrackingToggleSwitch.IsOn && _overlayPollTimer.IsEnabled
            ? _edgeForegroundSample : null;
        return new(
            session?.GameDisplayName ?? foreground?.ProcessName ?? selected?.DisplayName,
            session?.FrameRateProcessId ?? foreground?.ProcessId,
            FpsTrackingToggleSwitch.IsOn ? session?.FramesPerSecond ?? foreground?.FramesPerSecond : null,
            session is not null,
            !_isBusy && (session is not null || selected is not null),
            selected?.DisplayName,
            FpsOverlayToggleSwitch.IsOn,
            GetSelectedOverlayStyle(),
            VendorTelemetrySettingsToggleSwitch.IsOn,
            _history.FirstOrDefault()?.Summary,
            FpsTrackingToggleSwitch.IsOn ? _edgeFpsHistory.Read() : [],
            RootLayout.RequestedTheme);
    }

    async Task IEdgePanelActions.ToggleEdgePanelSessionAsync(XamlRoot dialogRoot)
    {
        using IDisposable silence = TacticalAudioService.SilencePanelAction();
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
        using IDisposable silence = TacticalAudioService.SilencePanelAction();
        SetFpsOverlayToggleValues(enabled);
        ApplyOverlaySettingsFromControls(scheduleSave: true);
    }

    void IEdgePanelActions.SetEdgePanelOverlayStyle(PerformanceOverlayStyle style)
    {
        using IDisposable silence = TacticalAudioService.SilencePanelAction();
        // Jedno zrodlo prawdy: wybor stylu na stronie Sesja.
        OverlayStyleSelector.SelectedIndex = GetOverlayStyleIndex(style);
        ApplyOverlaySettingsFromControls(scheduleSave: true);
    }
}

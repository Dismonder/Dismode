using Dismode.Core.Interaction;
using Dismode.Core.Profiles;
using Dismode.UI.Services;
using Dismode.Windows.Performance;
using Dismode.Windows.SystemOptimization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Dismode.UI;

public sealed partial class EdgePanelWindow : Window, IDisposable
{
    private const int ToggleHotkey = 0xD150;
    private const int EscapeHotkey = 0xD151;
    private readonly IEdgePanelActions _actions;
    private readonly EdgePanelNativeWindow _native;
    private readonly EdgePanelHandleWindow _handle;
    private readonly EdgePanelStateMachine _state = new(TimeProvider.System);
    private readonly EdgePanelHotkeyRegistration _toggleHotkey;
    private readonly EdgePanelHotkeyRegistration _escapeHotkey;
    private readonly OverlayTelemetrySampler _telemetry = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private RectInt32 _bounds;
    private EdgePanelState _drawnState;
    private Storyboard? _slide;
    private DateTimeOffset _nextContentUpdate;
    private bool _updating = true;
    private bool _dialogOpen;
    private bool _disposed;

    internal EdgePanelWindow(IEdgePanelActions actions)
    {
        _actions = actions;
        InitializeComponent();
        Title = "Dismode — panel przy krawędzi";
        _native = new(this);
        try
        {
            _handle = new();
            _toggleHotkey = new(() => _native.Register(ToggleHotkey, 0x0001, 0x44), () => _native.Unregister(ToggleHotkey));
            _escapeHotkey = new(() => _native.Register(EscapeHotkey, 0, 0x1B), () => _native.Unregister(EscapeHotkey));
            _native.HotkeyPressed += OnHotkey;
            if (!_toggleHotkey.TryRegister())
            {
                HotkeyText.Text = "Alt+D jest zajęty · Otwórz panel myszką";
            }

            Reposition();
            RefreshContent();
            _timer.Tick += OnTick;
            _timer.Start();
        }
        catch
        {
            // The caller never gets an instance to dispose, so release the
            // native windows and hotkeys created so far before rethrowing.
            _toggleHotkey?.Dispose();
            _escapeHotkey?.Dispose();
            _native.HotkeyPressed -= OnHotkey;
            _native.Dispose();
            _handle?.Dispose();
            _telemetry.Dispose();
            Close();
            throw;
        }
    }

    private void Reposition()
    {
        _handle.Reposition();
        RectInt32 work = EdgePanelNativeWindow.PrimaryWorkArea;
        double scale = _native.Scale;
        int width = (int)Math.Ceiling(340 * scale);
        int height = Math.Min((int)Math.Ceiling(800 * scale), work.Height - (int)(32 * scale));
        RectInt32 bounds = new(work.X + work.Width - width, work.Y + (work.Height - height) / 2, width, height);
        if (bounds != _bounds)
        {
            _bounds = bounds;
            _native.Position(bounds);
        }
    }

    private void OnTick(object? sender, object args)
    {
        bool visible = _state.State != EdgePanelState.Closed;
        _state.SetPointerInside(_dialogOpen || EdgePanelNativeWindow.PointerInside(_handle.HandleBounds)
            || (visible && EdgePanelNativeWindow.PointerInside(_bounds)));
        _state.Tick();
        DrawState();
        if (DateTimeOffset.UtcNow >= _nextContentUpdate)
        {
            Reposition();
            if (visible)
            {
                RefreshContent();
            }

            _nextContentUpdate = DateTimeOffset.UtcNow.AddSeconds(1);
        }
    }

    private void DrawState()
    {
        if (_drawnState != _state.State)
        {
            _drawnState = _state.State;
            if (_drawnState == EdgePanelState.Opening)
            {
                RefreshContent();
                _native.Reveal(_bounds.Width, _bounds.Height, _state.Reveal);
                _native.Show();
                _ = _escapeHotkey.TryRegister();
                TacticalAudioService.Instance.PlayPanelOpen();
                AnimateSlide(0);
            }
            else if (_drawnState == EdgePanelState.Closing)
            {
                _escapeHotkey.Release();
                TacticalAudioService.Instance.PlayPanelClose();
                AnimateSlide(_bounds.Width / _native.Scale);
            }
            else if (_drawnState == EdgePanelState.Closed)
            {
                _native.Hide();
                _telemetry.SetActive(false, null);
            }
        }

        if (_state.State != EdgePanelState.Closed)
        {
            _native.Reveal(_bounds.Width, _bounds.Height, _state.Reveal);
        }
    }

    private void AnimateSlide(double target)
    {
        _slide?.Stop();
        SlideTransform.X = _bounds.Width / _native.Scale * (1 - _state.Reveal);
        DoubleAnimation animation = new()
        {
            From = SlideTransform.X,
            To = target,
            Duration = new(EdgePanelStateMachine.AnimationDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, SlideTransform);
        Storyboard.SetTargetProperty(animation, "X");
        _slide = new();
        _slide.Children.Add(animation);
        _slide.Begin();
    }

    private void OnHotkey(int id)
    {
        if (_dialogOpen)
        {
            return;
        }

        if (id == ToggleHotkey)
        {
            _state.Toggle();
        }
        else if (id == EscapeHotkey)
        {
            _state.Close();
        }

        DrawState();
    }

    private void RefreshContent()
    {
        EdgePanelContent content = _actions.ReadEdgePanelContent();
        _updating = true;
        try
        {
            GameText.Text = content.GameName ?? "Brak aktywnej gry";
            FpsText.Text = Metric(content.FramesPerSecond, " FPS");
            SelectedGameText.Text = content.SessionActive ? "Tryb gry działa"
                : content.SelectedGameName is string selected ? $"Wybrana gra: {selected}" : "Wybierz grę w bibliotece";
            SessionButton.Content = content.SessionActive ? "Zakończ Tryb gry" : "Włącz Tryb gry";
            SessionButton.IsEnabled = content.CanToggleSession && !_dialogOpen;
            OverlayToggle.IsOn = content.OverlayEnabled;
            foreach (ToggleButton button in new[] { MinimalStyle, LowsStyle, CompactStyle, FullStyle })
            {
                button.IsChecked = button.Tag?.ToString() == content.OverlayStyle.ToString();
            }

            bool active = _state.State is EdgePanelState.Opening or EdgePanelState.Open;
            _telemetry.SetActive(active, content.ProcessId, content.VendorTelemetryEnabled);
            OverlayTelemetrySnapshot hardware = _telemetry.Snapshot;
            CpuText.Text = "CPU  " + Metric(hardware.Cpu?.CpuPercent, "%");
            GpuText.Text = "GPU  " + Metric(hardware.Gpu?.ThreeDPercent, "%");
            TemperatureText.Text = "GPU  " + Metric(hardware.Vendor?.TemperatureCelsius, " °C");
            long? ram = SystemMemoryUsageReader.TryReadUsedPhysicalMemoryBytes();
            RamText.Text = "RAM  " + (ram is long bytes ? $"{bytes / 1073741824d:0.0} GB" : "—");
            if (content.LastSession is { } last)
            {
                LastGameText.Text = last.GameDisplayName;
                LastDetailsText.Text = $"{last.Duration.TotalMinutes:0.#} min · śr. "
                    + Metric(last.FrameRateStatistics?.AverageFramesPerSecond, " FPS");
            }
            else
            {
                LastGameText.Text = "Jeszcze nie było sesji";
                LastDetailsText.Text = string.Empty;
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private static string Metric(double? value, string unit) => value is double number && double.IsFinite(number)
        ? $"{number:0}{unit}" : "—" + unit;

    private void OnCloseClicked(object sender, RoutedEventArgs args)
    {
        if (!_dialogOpen)
        {
            _state.Close();
            DrawState();
        }
    }

    private async void OnSessionClicked(object sender, RoutedEventArgs args)
    {
        if (_dialogOpen || _disposed)
        {
            return;
        }

        TacticalAudioService.Instance.PlayClick();
        _dialogOpen = true;
        SessionButton.IsEnabled = false;
        _escapeHotkey.Release();
        try
        {
            await _actions.ToggleEdgePanelSessionAsync(PanelRoot.XamlRoot);
        }
        finally
        {
            _dialogOpen = false;
            if (!_disposed)
            {
                _ = _escapeHotkey.TryRegister();
                RefreshContent();
            }
        }
    }

    private void OnOverlayToggled(object sender, RoutedEventArgs args)
    {
        if (!_updating)
        {
            TacticalAudioService.Instance.PlayToggle();
            _actions.SetEdgePanelOverlay(OverlayToggle.IsOn);
        }
    }

    private void OnStyleClicked(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton button && Enum.TryParse(button.Tag?.ToString(), out PerformanceOverlayStyle style))
        {
            TacticalAudioService.Instance.PlayClick();
            _actions.SetEdgePanelOverlayStyle(style);
            RefreshContent();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _slide?.Stop();
        _toggleHotkey.Dispose();
        _escapeHotkey.Dispose();
        _telemetry.Dispose();
        _native.Hide();
        _native.HotkeyPressed -= OnHotkey;
        _native.Dispose();
        _handle.Dispose();
        Close();
    }
}

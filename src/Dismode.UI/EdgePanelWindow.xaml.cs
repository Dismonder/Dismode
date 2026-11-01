using Dismode.Core.Interaction;
using Dismode.UI.Services;
using Dismode.Windows.Performance;
using Dismode.Windows.SystemOptimization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

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
    private readonly UISettings _uiSettings = new();
    private RectInt32 _bounds;
    private EdgePanelState _drawnState;
    private Storyboard? _slide;
    private Storyboard? _selection;
    private Storyboard? _bars;
    private DateTimeOffset _nextContentUpdate;
    private IReadOnlyList<(double AgeSeconds, double Fps)> _fpsHistory = [];
    private double? _displayFps;
    private double? _targetFps;
    private double? _fromFps;
    private long _fpsStarted;
    private int _selectedSegment = -1;
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
            _native.GeometryChanged += OnGeometryChanged;
            _handle.PointerChanged += OnHandlePointerChanged;
            HotkeyWarning.Visibility = _toggleHotkey.TryRegister() ? Visibility.Collapsed : Visibility.Visible;
            _uiSettings.AdvancedEffectsEnabledChanged += OnAdvancedEffectsChanged;
            _timer.Tick += OnTick;
            Reposition();
        }
        catch
        {
            // Konstruktor zwalnia zasoby, zanim przekaze blad do okna glownego.
            _toggleHotkey?.Dispose();
            _escapeHotkey?.Dispose();
            _native.HotkeyPressed -= OnHotkey;
            _native.GeometryChanged -= OnGeometryChanged;
            _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsChanged;
            _native.Dispose();
            _handle?.Dispose();
            _telemetry.Dispose();
            Close();
            throw;
        }
    }

    private bool Animate => TacticalVfxService.Instance.IsEnabled && _uiSettings.AnimationsEnabled;

    internal void SetHandleTransparency(int percent) => _handle.SetTransparency(percent);

    internal void OnForegroundChanged()
    {
        if (_disposed)
        {
            return;
        }

        // Okno gry moze przykryc uchwyt i pas krawedzi w tym samym pasmie topmost.
        _handle.AssertTopmost();
        if (_state.State != EdgePanelState.Closed)
        {
            _native.AssertTopmost();
        }
    }

    private void OnGeometryChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed)
        {
            Reposition();
        }
    });

    private void OnAdvancedEffectsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed && _state.State != EdgePanelState.Closed)
        {
            ApplyBackdrop();
        }
    });

    private void ApplyBackdrop()
    {
        bool glass = _uiSettings.AdvancedEffectsEnabled
            && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
        if (glass)
        {
            if (SystemBackdrop is not EdgePanelBackdrop)
            {
                SystemBackdrop = new EdgePanelBackdrop();
            }
        }
        else if (SystemBackdrop is not TransparentBackdrop)
        {
            SystemBackdrop = new TransparentBackdrop();
        }

        string theme = PanelRoot.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        ResourceDictionary colors = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        global::Windows.UI.Color tint = ((SolidColorBrush)colors["DismodeTitleBarBrush"]).Color;
        PanelRoot.Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(glass ? (byte)190 : (byte)255, tint.R, tint.G, tint.B));
    }

    private void Reposition()
    {
        _handle.Reposition();
        RectInt32 work = _handle.WorkArea;
        double scale = _native.Scale;
        const double widthDip = 300;
        double maximumHeight = work.Height / scale * 0.9;
        PanelScroll.MaxHeight = Math.Max(1, maximumHeight - 2);
        PanelRows.Measure(new global::Windows.Foundation.Size(widthDip - 2, double.PositiveInfinity));
        int width = (int)Math.Ceiling(widthDip * scale);
        int height = (int)Math.Ceiling(Math.Min(maximumHeight, PanelRows.DesiredSize.Height + 2) * scale);
        RectInt32 bounds = new(work.X + work.Width - width - (int)(16 * scale), work.Y + (work.Height - height) / 2, width, height);
        if (bounds != _bounds)
        {
            _bounds = bounds;
            _native.Position(bounds);
        }
    }

    private bool IsPointerInOpenPanel() =>
        EdgePanelNativeWindow.PointerInsideOpenPanel(
            EdgePanelNativeWindow.ToEdgeRect(_bounds),
            _handle.MonitorBounds,
            _handle.HandleRect,
            _native.Scale,
            _state.HasPointerEntered);

    private void OnHandlePointerChanged(bool inside)
    {
        _state.AnimationsEnabled = Animate;
        _state.SetPointerInside(inside || (_state.State != EdgePanelState.Closed && IsPointerInOpenPanel()));
        DrawState();
    }

    private void OnTick(object? sender, object args)
    {
        _state.AnimationsEnabled = Animate;
        _state.SetPointerInside(_state.State == EdgePanelState.Closed
            ? EdgePanelNativeWindow.PointerInsideHotZone(_handle.MonitorBounds, _handle.HandleRect)
            : IsPointerInOpenPanel());
        _state.Tick();
        DrawState();
        if (_state.State is EdgePanelState.Opening or EdgePanelState.Open)
        {
            if (DateTimeOffset.UtcNow >= _nextContentUpdate)
            {
                RefreshContent();
            }

            TweenFps();
        }
    }

    private void DrawState()
    {
        if (_drawnState != _state.State)
        {
            EdgePanelState previous = _drawnState;
            _drawnState = _state.State;
            if (_drawnState == EdgePanelState.Opening || (_drawnState == EdgePanelState.Open && previous == EdgePanelState.Closed))
            {
                RefreshContent();
                Reposition();
                _handle.Show();
                _native.Show();
                _ = _escapeHotkey.TryRegister();
                AnimateCard(true);
            }
            else if (_drawnState == EdgePanelState.Closing)
            {
                _escapeHotkey.Release();
                _telemetry.SetActive(false, null);
                AnimateCard(false);
            }
            else if (_drawnState == EdgePanelState.Closed)
            {
                _escapeHotkey.Release();
                _native.Hide();
                _handle.Show();
                // Po schowaniu karty tylko uchwyt i pas krawedzi moga blokowac ponowne otwarcie.
                _state.SetPointerInside(EdgePanelNativeWindow.PointerInsideHotZone(_handle.MonitorBounds, _handle.HandleRect));
                _telemetry.SetActive(false, null);
            }
        }

        if (_state.NeedsTimer)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void AnimateCard(bool opening)
    {
        _slide?.Stop();
        if (!Animate)
        {
            CardTransform.TranslateX = 0;
            CardTransform.ScaleX = CardTransform.ScaleY = PanelRoot.Opacity = 1;
            foreach (UIElement row in PanelRows.Children)
            {
                row.Opacity = 1;
            }
            return;
        }

        _slide = new();
        TimeSpan duration = opening ? EdgePanelStateMachine.AnimationDuration : EdgePanelStateMachine.CloseAnimationDuration;
        AddAnimation(_slide, CardTransform, "TranslateX", opening ? 32 * (1 - _state.Reveal) : 0, opening ? 0 : 32, duration, opening);
        AddAnimation(_slide, CardTransform, "ScaleX", opening ? 0.97 : 1, opening ? 1 : 0.98, duration, opening);
        AddAnimation(_slide, CardTransform, "ScaleY", opening ? 0.97 : 1, opening ? 1 : 0.98, duration, opening);
        AddAnimation(_slide, PanelRoot, "Opacity", opening ? _state.Reveal : 1, opening ? 1 : 0, duration, opening);
        for (int i = 0; i < PanelRows.Children.Count; i++)
        {
            UIElement row = PanelRows.Children[i];
            row.Opacity = 1;
            if (opening)
            {
                AddAnimation(_slide, row, "Opacity", 0, 1, TimeSpan.FromMilliseconds(150), true, TimeSpan.FromMilliseconds(i * 35));
            }
        }
        _slide.Begin();
    }

    private static void AddAnimation(Storyboard storyboard, DependencyObject target, string property, double from, double to,
        TimeSpan duration, bool opening = true, TimeSpan? delay = null)
    {
        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new(duration),
            BeginTime = delay ?? TimeSpan.Zero,
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void OnHotkey(int id)
    {
        if (_state.IsDialogOpen)
        {
            return;
        }

        _state.AnimationsEnabled = Animate;
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
        PanelRoot.RequestedTheme = content.Theme;
        ApplyBackdrop();
        GameText.Text = content.GameName ?? content.SelectedGameName ?? "Brak aktywnej gry";
        double? fps = Valid(content.FramesPerSecond);
        if (_targetFps != fps)
        {
            _fromFps = _displayFps;
            _targetFps = fps;
            _fpsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        _fpsHistory = content.FpsHistory ?? [];
        TweenFps();
        RenderSparkline();
        SessionLabel.Text = content.SessionActive ? "Zakończ Tryb gry" : "Włącz Tryb gry";
        SessionGlyph.Glyph = content.SessionActive ? "\uE71A" : "\uE768";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SessionButton, SessionLabel.Text);
        SessionButton.IsEnabled = content.CanToggleSession && !_state.IsDialogOpen;
        SelectSegment(EdgePanelMetrics.GetOverlaySegment(content.OverlayEnabled, content.OverlayStyle));
        _telemetry.SetActive(true, content.ProcessId, content.VendorTelemetryEnabled);
        OverlayTelemetrySnapshot hardware = _telemetry.Snapshot;
        CpuText.Text = Metric(hardware.Cpu?.CpuPercent, "%");
        GpuText.Text = Metric(hardware.Gpu?.ThreeDPercent, "%");
        AnimateBars(hardware.Cpu?.CpuPercent, hardware.Gpu?.ThreeDPercent);
        TemperatureText.Text = Valid(hardware.Vendor?.TemperatureCelsius) is double celsius ? $"{celsius:0} °C" : "— °C";
        long? ram = SystemMemoryUsageReader.TryReadUsedPhysicalMemoryBytes();
        RamText.Text = "RAM  " + (ram is long bytes ? $"{bytes / 1073741824d:0.0} GB" : "—");
        LastSessionText.Visibility = content.LastSession is null ? Visibility.Collapsed : Visibility.Visible;
        if (content.LastSession is { } last)
        {
            TimeSpan duration = last.Duration;
            string elapsed = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} h {duration.Minutes} min" : $"{(int)duration.TotalMinutes} min";
            LastSessionText.Text = $"Ostatnio: {last.GameDisplayName} · {elapsed}";
        }
        Reposition();
        _nextContentUpdate = DateTimeOffset.UtcNow.AddSeconds(1);
    }

    private void TweenFps()
    {
        if (_targetFps is not double target || _fromFps is not double from || !Animate)
        {
            _displayFps = _targetFps;
        }
        else
        {
            double progress = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_fpsStarted).TotalMilliseconds / 200, 0, 1);
            _displayFps = from + ((target - from) * (1 - Math.Pow(1 - progress, 3)));
        }
        FpsText.Text = Metric(_displayFps, string.Empty);
    }

    private void AnimateBars(double? cpu, double? gpu)
    {
        double fromCpu = CpuScale.ScaleX;
        double fromGpu = GpuScale.ScaleX;
        _bars?.Stop();
        double toCpu = Math.Clamp(Valid(cpu) ?? 0, 0, 100) / 100;
        double toGpu = Math.Clamp(Valid(gpu) ?? 0, 0, 100) / 100;
        CpuScale.ScaleX = toCpu;
        GpuScale.ScaleX = toGpu;
        if (Animate)
        {
            _bars = new();
            AddAnimation(_bars, CpuScale, "ScaleX", fromCpu, toCpu, TimeSpan.FromMilliseconds(200));
            AddAnimation(_bars, GpuScale, "ScaleX", fromGpu, toGpu, TimeSpan.FromMilliseconds(200));
            _bars.Begin();
        }
    }

    private void SelectSegment(int segment)
    {
        if (_selectedSegment == segment)
        {
            return;
        }
        double from = SelectionTransform.X;
        _selection?.Stop();
        _selectedSegment = segment;
        SelectionTransform.X = segment * 52;
        if (Animate)
        {
            _selection = new();
            AddAnimation(_selection, SelectionTransform, "X", from, segment * 52, TimeSpan.FromMilliseconds(180));
            _selection.Begin();
        }
        if (OverlaySegment.Children[1] is StackPanel buttons)
        {
            for (int i = 0; i < buttons.Children.Count; i++)
            {
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(buttons.Children[i], i == segment ? "Wybrana" : string.Empty);
            }
        }
    }

    private void OnFpsCanvasSizeChanged(object sender, SizeChangedEventArgs args) => RenderSparkline();

    private void RenderSparkline()
    {
        FpsSparkline.Points.Clear();
        if (_fpsHistory.Count < 2 || FpsCanvas.ActualWidth <= 0)
        {
            return;
        }
        double span = Math.Clamp(_fpsHistory[0].AgeSeconds, 1, 60);
        double min = _fpsHistory.Min(sample => sample.Fps);
        double range = Math.Max(1, _fpsHistory.Max(sample => sample.Fps) - min);
        foreach ((double age, double fps) in _fpsHistory)
        {
            FpsSparkline.Points.Add(new(Math.Clamp(1 - (age / span), 0, 1) * FpsCanvas.ActualWidth,
                28 - ((fps - min) / range * 24)));
        }
    }

    private static double? Valid(double? value) => value is double number && double.IsFinite(number) && number >= 0 ? number : null;
    private static string Metric(double? value, string unit) => Valid(value) is double number ? $"{number:0}{unit}" : "—";

    private void OnCloseClicked(object sender, RoutedEventArgs args)
    {
        if (!_state.IsDialogOpen)
        {
            _state.AnimationsEnabled = Animate;
            _state.Close();
            DrawState();
        }
    }

    private async void OnSessionClicked(object sender, RoutedEventArgs args)
    {
        if (_state.IsDialogOpen || _disposed)
        {
            return;
        }
        _state.SetDialogOpen(true);
        SessionButton.IsEnabled = false;
        _escapeHotkey.Release();
        try
        {
            await _actions.ToggleEdgePanelSessionAsync(PanelRoot.XamlRoot);
        }
        finally
        {
            _state.SetDialogOpen(false);
            if (!_disposed)
            {
                _ = _escapeHotkey.TryRegister();
                RefreshContent();
            }
        }
    }

    private void OnSegmentClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out int segment))
        {
            if (EdgePanelMetrics.GetOverlayStyle(segment) is { } style)
            {
                _actions.SetEdgePanelOverlayStyle(style);
            }
            _actions.SetEdgePanelOverlay(segment != 0);
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
        _selection?.Stop();
        _bars?.Stop();
        _toggleHotkey.Dispose();
        _escapeHotkey.Dispose();
        _telemetry.Dispose();
        SystemBackdrop = null;
        _uiSettings.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsChanged;
        _native.Hide();
        _native.HotkeyPressed -= OnHotkey;
        _native.GeometryChanged -= OnGeometryChanged;
        _handle.PointerChanged -= OnHandlePointerChanged;
        _native.Dispose();
        _handle.Dispose();
        Close();
    }
}

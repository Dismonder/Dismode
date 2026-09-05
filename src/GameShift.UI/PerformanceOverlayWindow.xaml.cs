using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GameShift.Core.Profiles;
using GameShift.UI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT.Interop;

namespace GameShift.UI;

public sealed partial class PerformanceOverlayWindow : Window, IDisposable
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint LwaColorKey = 0x00000001;
    private const uint TransparentColorKey = 0x00000000;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private const int DwmwaBorderColor = 34;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);
    private const int OverlayWidth = 328;
    private const int OverlayHeight = 120;
    private const int OverlayMargin = 22;
    private static readonly TimeSpan ForegroundPollInterval =
        TimeSpan.FromMilliseconds(200);
    private const double GraphWidth = 58d;
    private const double GraphHeight = 17d;
    private const int MaximumGraphSamples = 32;
    private static readonly nint HwndTopmost = new(-1);

    private static readonly Color Cyan =
        Color.FromArgb(255, 53, 242, 208);
    private static readonly Color Green =
        Color.FromArgb(255, 59, 220, 155);
    private static readonly Color Amber =
        Color.FromArgb(255, 244, 184, 74);
    private static readonly Color Red =
        Color.FromArgb(255, 255, 91, 116);

    private readonly nint _windowHandle;
    private readonly Queue<double> _frameTimeHistory = [];
    private readonly DispatcherTimer _foregroundTimer = new()
    {
        Interval = ForegroundPollInterval,
    };
    private string? _gameDisplayName;
    private int? _targetProcessId;
    private PerformanceOverlayCorner _corner =
        PerformanceOverlayCorner.TopRight;
    private PerformanceOverlayStyle _style =
        PerformanceOverlayStyle.FullDeck;
    private PerformanceOverlayTheme _theme =
        PerformanceOverlayTheme.CyberNeon;
    private double _scale = 1d;
    private nint _positionedMonitor;
    private uint _positionedDpi;
    private bool _requestedVisible;
    private bool _isShown;
    private bool _layoutDirty = true;
    private bool _disposed;

    private int CurrentBaseWidth => _style switch
    {
        PerformanceOverlayStyle.CompactBar => 190,
        PerformanceOverlayStyle.MinimalText => 120,
        _ => OverlayWidth,
    };

    private int CurrentBaseHeight => _style switch
    {
        PerformanceOverlayStyle.CompactBar => 52,
        PerformanceOverlayStyle.MinimalText => 44,
        _ => OverlayHeight,
    };

    public PerformanceOverlayWindow()
    {
        InitializeComponent();
        Title = "GameShift Performance";
        _windowHandle = WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(
                hasBorder: false,
                hasTitleBar: false);
        }

        nint extendedStyle = GetWindowLongPtr(_windowHandle, GwlExStyle);
        _ = SetWindowLongPtr(
            _windowHandle,
            GwlExStyle,
            extendedStyle
            | (nint)(
                WsExTopmost
                | WsExTransparent
                | WsExToolWindow
                | WsExLayered
                | WsExNoActivate));
        if (!SetLayeredWindowAttributes(
                _windowHandle,
                TransparentColorKey,
                byte.MaxValue,
                LwaColorKey))
        {
            throw new InvalidOperationException(
                "Windows nie ustawił przezroczystości nakładki.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        ApplyWindowOpacity(
            PerformanceOverlayPreferences.DefaultOpacityPercent);

        int cornerPreference = DwmWindowCornerPreferenceDoNotRound;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        int borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmwaBorderColor,
            ref borderColor,
            sizeof(int));

        _foregroundTimer.Tick += OnForegroundTimerTick;
    }

    public void Update(SessionStateClientSnapshot session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        if (!StringComparer.Ordinal.Equals(
                _gameDisplayName,
                session.GameDisplayName))
        {
            _gameDisplayName = session.GameDisplayName;
            _frameTimeHistory.Clear();
        }

        if (_targetProcessId != session.FrameRateProcessId)
        {
            _targetProcessId = session.FrameRateProcessId;
            _layoutDirty = true;
        }

        GameNameText.Text = session.GameDisplayName;
        UpdateAutoRestoreVisual(session);

        Color themeAccent = GetThemeAccentColor(_theme);

        if (session.FramesPerSecond is double framesPerSecond
            && session.FrameTimeMilliseconds is double frameTime)
        {
            string fpsString = framesPerSecond.ToString(
                "0",
                CultureInfo.CurrentCulture);
            string frameTimeString = frameTime.ToString(
                "0.00",
                CultureInfo.CurrentCulture);

            FpsValueText.Text = fpsString;
            CompactFpsValueText.Text = fpsString;
            MinimalFpsValueText.Text = fpsString;

            FrameTimeValueText.Text = frameTimeString;
            CompactFrameTimeValueText.Text = frameTimeString;

            Color metricColor = GetMetricColor(frameTime);
            FpsValueText.Foreground = new SolidColorBrush(themeAccent);
            CompactFpsValueText.Foreground = new SolidColorBrush(themeAccent);
            MinimalFpsValueText.Foreground = new SolidColorBrush(themeAccent);

            FrameTimeValueText.Foreground = new SolidColorBrush(metricColor);
            CompactFrameTimeValueText.Foreground = new SolidColorBrush(metricColor);
            FrameGraphLine.Stroke = new SolidColorBrush(metricColor);

            SetStatusVisual(
                "LIVE",
                Green);
            SourceStatusText.Text =
                $"PRESENTMON 2.5.1 • PID {session.FrameRateProcessId}";
            AddFrameTimeSample(frameTime);
            RefreshVisibility();
            return;
        }

        FpsValueText.Text = "—";
        CompactFpsValueText.Text = "—";
        MinimalFpsValueText.Text = "—";

        FrameTimeValueText.Text = "—";
        CompactFrameTimeValueText.Text = "—";

        _frameTimeHistory.Clear();
        UpdateFrameGraph();
        bool failed = IsFailureStatus(session.FrameRateStatus);
        Color stateColor = failed ? Red : Amber;
        SetStatusVisual(
            failed ? "BRAK" : "SYNC",
            stateColor);
        SourceStatusText.Text = failed
            ? "ŹRÓDŁO FPS NIEDOSTĘPNE"
            : session.FrameRateProcessId is int processId
                ? $"SYNCHRONIZACJA ETW • PID {processId}"
                : "OCZEKIWANIE NA PROCES GRY";
        RefreshVisibility();
    }

    public void ApplyPreferences(
        PerformanceOverlayPreferences preferences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(preferences);
        double scale = preferences.ScalePercent / 100d;
        if (Math.Abs(_scale - scale) > double.Epsilon
            || _corner != preferences.Corner
            || _style != preferences.Style)
        {
            _scale = scale;
            _corner = preferences.Corner;
            _style = preferences.Style;
            _layoutDirty = true;
        }

        _theme = preferences.Theme;

        OverlayRoot.Width = CurrentBaseWidth;
        OverlayRoot.Height = CurrentBaseHeight;

        ApplyStyleAndThemeLayout(preferences);
        ApplyWindowOpacity(preferences.OpacityPercent);
        OverlayScaleTransform.ScaleX = _scale;
        OverlayScaleTransform.ScaleY = _scale;
        SetRequestedVisibility(
            preferences.IsEnabled && preferences.IsFpsTrackingEnabled);
    }

    private void ApplyStyleAndThemeLayout(PerformanceOverlayPreferences preferences)
    {
        Color themeAccent = GetThemeAccentColor(preferences.Theme);
        SolidColorBrush accentBrush = new(themeAccent);

        FpsValueText.Foreground = accentBrush;
        CompactFpsValueText.Foreground = accentBrush;
        MinimalFpsValueText.Foreground = accentBrush;
        FrameTimeIndicatorBar.Background = accentBrush;

        switch (preferences.Style)
        {
            case PerformanceOverlayStyle.CompactBar:
                OverlaySurfaceBorder.Visibility = Visibility.Collapsed;
                CompactBarLayout.Visibility = Visibility.Visible;
                MinimalTextLayout.Visibility = Visibility.Collapsed;
                break;

            case PerformanceOverlayStyle.MinimalText:
                OverlaySurfaceBorder.Visibility = Visibility.Collapsed;
                CompactBarLayout.Visibility = Visibility.Collapsed;
                MinimalTextLayout.Visibility = Visibility.Visible;
                break;

            default:
                OverlaySurfaceBorder.Visibility = Visibility.Visible;
                CompactBarLayout.Visibility = Visibility.Collapsed;
                MinimalTextLayout.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static Color GetThemeAccentColor(PerformanceOverlayTheme theme) => theme switch
    {
        PerformanceOverlayTheme.MatrixGreen => Color.FromArgb(255, 61, 203, 112),
        PerformanceOverlayTheme.ToxicGreen => Color.FromArgb(255, 0, 255, 102),
        PerformanceOverlayTheme.ApexAmber => Color.FromArgb(255, 255, 165, 31),
        PerformanceOverlayTheme.CrimsonRed => Color.FromArgb(255, 255, 77, 109),
        PerformanceOverlayTheme.PureWhite => Color.FromArgb(255, 255, 255, 255),
        PerformanceOverlayTheme.StealthPurple => Color.FromArgb(255, 168, 85, 247),
        _ => Color.FromArgb(255, 53, 242, 208), // CyberNeon
    };

    private void ApplyWindowOpacity(int opacityPercent)
    {
        double opacity = Math.Clamp(opacityPercent, 0, 100) / 100d;
        OverlayStatisticsContent.Opacity = opacity;
    }

    public void SetRequestedVisibility(bool requestedVisible)
    {
        if (_disposed)
        {
            return;
        }

        _requestedVisible = requestedVisible;
        if (requestedVisible)
        {
            if (!_foregroundTimer.IsEnabled)
            {
                _foregroundTimer.Start();
            }
        }
        else
        {
            _foregroundTimer.Stop();
        }

        RefreshVisibility();
    }

    private void OnForegroundTimerTick(object? sender, object args) =>
        RefreshVisibility();

    private void RefreshVisibility()
    {
        if (_disposed)
        {
            return;
        }

        if (!_requestedVisible || !IsTargetGameForeground())
        {
            HideWindow();
            return;
        }

        ShowForGame();
    }

    private bool IsTargetGameForeground()
    {
        if (_targetProcessId is not int targetProcessId
            || targetProcessId <= 0)
        {
            return false;
        }

        nint foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(
            foregroundWindow,
            out uint foregroundProcessId);
        return foregroundProcessId == (uint)targetProcessId;
    }

    private void ShowForGame()
    {
        nint gameWindow = ResolveTargetWindowHandle();
        nint monitor = MonitorFromWindow(
            gameWindow != nint.Zero ? gameWindow : _windowHandle,
            gameWindow != nint.Zero
                ? MonitorDefaultToNearest
                : MonitorDefaultToPrimary);
        MonitorInfo monitorInfo = new()
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException(
                "Windows nie zwrócił obszaru monitora dla nakładki.");
        }

        uint initialDpi = GetDpiForWindow(_windowHandle);
        if (!_isShown
            || _layoutDirty
            || _positionedMonitor != monitor
            || _positionedDpi != initialDpi)
        {
            PositionOverlay(monitorInfo, initialDpi);
        }

        uint positionedDpi = GetDpiForWindow(_windowHandle);
        if (positionedDpi != initialDpi)
        {
            PositionOverlay(monitorInfo, positionedDpi);
        }

        _positionedMonitor = monitor;
        _positionedDpi = positionedDpi;
        _layoutDirty = false;
        _isShown = true;
    }

    private void PositionOverlay(
        MonitorInfo monitorInfo,
        uint dpi)
    {
        int width = ScaleForDpi(
            checked((int)Math.Round(
                CurrentBaseWidth * _scale,
                MidpointRounding.AwayFromZero)),
            dpi);
        int height = ScaleForDpi(
            checked((int)Math.Round(
                CurrentBaseHeight * _scale,
                MidpointRounding.AwayFromZero)),
            dpi);
        int margin = ScaleForDpi(OverlayMargin, dpi);
        bool alignLeft = _corner is
            PerformanceOverlayCorner.TopLeft
            or PerformanceOverlayCorner.BottomLeft;
        bool alignTop = _corner is
            PerformanceOverlayCorner.TopLeft
            or PerformanceOverlayCorner.TopRight;
        int x = alignLeft
            ? monitorInfo.Monitor.Left + margin
            : monitorInfo.Monitor.Right - width - margin;
        int y = alignTop
            ? monitorInfo.Monitor.Top + margin
            : monitorInfo.Monitor.Bottom - height - margin;
        if (!SetWindowPos(
                _windowHandle,
                HwndTopmost,
                x,
                y,
                width,
                height,
                SwpNoActivate | SwpShowWindow))
        {
            throw new InvalidOperationException(
                "Windows nie wyświetlił nakładki wydajności.");
        }
    }

    public void HideOverlay()
    {
        if (_disposed)
        {
            return;
        }

        SetRequestedVisibility(false);
    }

    private void HideWindow()
    {
        if (!_isShown)
        {
            return;
        }

        _ = ShowWindow(_windowHandle, SwHide);
        _isShown = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTimerTick;
        Close();
        GC.SuppressFinalize(this);
    }

    private void AddFrameTimeSample(double frameTime)
    {
        _frameTimeHistory.Enqueue(frameTime);
        while (_frameTimeHistory.Count > MaximumGraphSamples)
        {
            _ = _frameTimeHistory.Dequeue();
        }

        UpdateFrameGraph();
    }

    private void UpdateFrameGraph()
    {
        FrameGraphLine.Points.Clear();
        if (_frameTimeHistory.Count == 0)
        {
            return;
        }

        double[] samples = [.. _frameTimeHistory];
        double ceiling = Math.Max(
            33.33d,
            samples.Max() * 1.15d);
        if (samples.Length == 1)
        {
            double y = ScaleFrameTime(samples[0], ceiling);
            FrameGraphLine.Points.Add(new(0d, y));
            FrameGraphLine.Points.Add(new(GraphWidth, y));
            return;
        }

        double step = GraphWidth / (samples.Length - 1);
        for (int index = 0; index < samples.Length; index++)
        {
            FrameGraphLine.Points.Add(
                new(
                    index * step,
                    ScaleFrameTime(samples[index], ceiling)));
        }
    }

    private static double ScaleFrameTime(
        double frameTime,
        double ceiling) =>
        GraphHeight
        - Math.Clamp(frameTime / ceiling, 0d, 1d) * GraphHeight;

    private void SetStatusVisual(
        string label,
        Color foreground)
    {
        StatusBadgeText.Text = label;
        StatusBadgeText.Foreground = new SolidColorBrush(foreground);
        SourceIndicator.Fill = new SolidColorBrush(foreground);
    }

    private void UpdateAutoRestoreVisual(
        SessionStateClientSnapshot session)
    {
        Color color;
        string status;
        string detail;

        if (session.ErrorCount > 0
            || string.Equals(
                session.State,
                "RecoveryRequired",
                StringComparison.OrdinalIgnoreCase))
        {
            color = Red;
            status = "WYMAGA UWAGI";
            detail = $"BŁĘDY: {session.ErrorCount}";
        }
        else if (session.ConflictCount > 0)
        {
            color = Amber;
            status = "KONFLIKT";
            detail = $"DO SPRAWDZENIA: {session.ConflictCount}";
        }
        else if (string.Equals(
                     session.State,
                     "Restoring",
                     StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     session.State,
                     "Reconciling",
                     StringComparison.OrdinalIgnoreCase)
                 || string.Equals(
                     session.State,
                     "GameExited",
                     StringComparison.OrdinalIgnoreCase))
        {
            color = Cyan;
            status = "PRZYWRACANIE";
            detail = $"{session.RestoredActionCount}/{session.AppliedActionCount} AKCJI";
        }
        else
        {
            color = Green;
            status = "UZBROJONE";
            int pendingActions = Math.Max(
                0,
                session.AppliedActionCount - session.RestoredActionCount);
            detail = pendingActions == 0
                ? "MONITORING WYJŚCIA"
                : $"CHRONIONE AKCJE: {pendingActions}";
        }

        AutoRestoreIndicator.Fill = new SolidColorBrush(color);
        AutoRestoreStatusText.Foreground = new SolidColorBrush(color);
        AutoRestoreStatusText.Text = status;
        AutoRestoreDetailText.Text = detail;
    }

    private nint ResolveTargetWindowHandle()
    {
        if (_targetProcessId is not int processId)
        {
            return nint.Zero;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited
                ? nint.Zero
                : process.MainWindowHandle;
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            return nint.Zero;
        }
    }

    private static Color GetMetricColor(double frameTime) =>
        frameTime switch
        {
            <= 18.5d => Cyan,
            <= 25d => Amber,
            _ => Red,
        };

    private static bool IsFailureStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return status.Contains("błąd", StringComparison.OrdinalIgnoreCase)
            || status.Contains(
                "niedostęp",
                StringComparison.OrdinalIgnoreCase)
            || status.Contains(
                "nieprawidł",
                StringComparison.OrdinalIgnoreCase)
            || status.Contains(
                "odmówił",
                StringComparison.OrdinalIgnoreCase)
            || status.Contains(
                "zakończył pomiar",
                StringComparison.OrdinalIgnoreCase);
    }

    private static int ScaleForDpi(int value, uint dpi) =>
        checked((int)Math.Round(
            value * Math.Max(96U, dpi) / 96d,
            MidpointRounding.AwayFromZero));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(
        nint windowHandle,
        int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(
        nint windowHandle,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(
        nint windowHandle,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int value,
        int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rectangle Monitor;
        internal Rectangle Work;
        internal uint Flags;
    }
}

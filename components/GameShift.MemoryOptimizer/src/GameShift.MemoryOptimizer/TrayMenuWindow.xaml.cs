using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace GameShift.MemoryOptimizer;

internal sealed partial class TrayMenuWindow : Window
{
    private const int BaseWidth = 400;
    private const int BaseHeight = 548;
    private const uint MonitorDefaultToNearest = 2;
    private const uint DpiDefault = 96;
    private readonly nint _windowHandle;
    private readonly Action _open;
    private readonly Func<Task> _optimize;
    private readonly Func<Task> _togglePause;
    private readonly Func<Task<bool>> _disable;
    private readonly Action _exit;
    private bool _isShown;
    private bool _disableInProgress;
    private bool _isClosed;

    internal TrayMenuWindow(
        MemoryOptimizerStatus? status,
        bool canOptimize,
        bool canPause,
        Action open,
        Func<Task> optimize,
        Func<Task> togglePause,
        Func<Task<bool>> disable,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(optimize);
        ArgumentNullException.ThrowIfNull(togglePause);
        ArgumentNullException.ThrowIfNull(disable);
        ArgumentNullException.ThrowIfNull(exit);

        InitializeComponent();
        _windowHandle = WindowNative.GetWindowHandle(this);
        _open = open;
        _optimize = optimize;
        _togglePause = togglePause;
        _disable = disable;
        _exit = exit;
        Title = "GameShift Memory Optimizer";
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

        Activated += OnActivated;
        Closed += OnClosed;
        SetWindowAttribute(20, 1); // DWMWA_USE_IMMERSIVE_DARK_MODE
        SetWindowAttribute(33, 2); // DWMWA_WINDOW_CORNER_PREFERENCE: ROUND
        SetWindowAttribute(34, new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            ? uint.MaxValue : 0xFFFFFFFE); // Default border in High Contrast; otherwise no DWM border.
        UpdateState(status, canOptimize, canPause);
    }

    internal event EventHandler? Dismissed;

    internal void VerifyLayoutForStartupProbe()
    {
        // Measure the real XAML without activating a window or touching the desktop.
        MenuRoot.Measure(new Windows.Foundation.Size(BaseWidth, BaseHeight));
        MenuRoot.Arrange(new Windows.Foundation.Rect(0, 0, BaseWidth, BaseHeight));
        MenuRoot.UpdateLayout();
        if (MenuRoot.DesiredSize.Width > BaseWidth || MenuRoot.DesiredSize.Height > BaseHeight ||
            ActionButtonsPanel.DesiredSize.Height > ActionArea.ActualHeight + 1)
        {
            throw new InvalidOperationException("Memory Optimizer tray content exceeds its window bounds.");
        }
    }

    internal void UpdateState(MemoryOptimizerStatus? status, bool canOptimize, bool canPause)
    {
        if (_isClosed)
        {
            return;
        }

        TrayMenuState state = TrayMenuState.Create(status, canOptimize, canPause, CultureInfo.CurrentCulture);
        MemoryPercentText.Text = state.PercentText;
        AvailableMemoryText.Text = state.AvailableText;
        MemoryUsageText.Text = state.UsageText;
        RamProgressBar.Value = state.Percent ?? 0;
        RamProgressBar.Opacity = state.Percent.HasValue ? 1 : 0.25;
        MemoryPressureText.Text = state.PressureText;
        MemoryStatusText.Text = state.StatusText;
        ServiceDetailText.Text = state.DetailText;
        ToolTipService.SetToolTip(ServiceDetailText, state.DetailText);
        ServiceStatusDot.Fill = (Brush)Application.Current.Resources[state.StatusBrush];
        MemoryPercentText.Foreground = (Brush)Application.Current.Resources[state.PressureBrush];
        RamProgressBar.Foreground = MemoryPercentText.Foreground;
        PauseMenuTitle.Text = state.PauseText;
        PauseMenuIcon.Glyph = status?.IsPaused == true ? "\uE768" : "\uE769";
        OptimizeMenuTitle.Text = state.IsBusy ? "Optymalizacja w toku…" : "Optymalizuj zapisany profil";
        OptimizeMenuButton.IsEnabled = state.CanOptimize;
        PauseMenuButton.IsEnabled = state.CanPause;
        OptimizeProgress.IsActive = state.IsBusy;
        OptimizeProgress.Opacity = state.IsBusy ? 1 : 0;
    }

    private void SetWindowAttribute(uint attribute, uint value)
    {
        int result = DwmSetWindowAttribute(_windowHandle, attribute, ref value, sizeof(uint));
        if (result < 0)
        {
            Debug.WriteLine($"Tray window attribute {attribute}: HRESULT 0x{result:X8}");
        }
    }

    internal void ShowAtCursor()
    {
        if (_isShown)
        {
            Activate();
            return;
        }

        _isShown = true;
        PositionAtCursor();
        Activate();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed)
            {
                return;
            }
            _ = SetForegroundWindow(_windowHandle);
            OptimizeMenuButton.Focus(FocusState.Programmatic);
        });
    }

    internal void CloseMenu()
    {
        if (!_isShown)
        {
            return;
        }

        _isShown = false;
        Close();
    }

    private void PositionAtCursor()
    {
        if (!GetCursorPos(out Point cursor))
        {
            return;
        }

        // Move the still-hidden window to the target monitor before reading its DPI.
        AppWindow.Move(new(cursor.X, cursor.Y));
        uint dpi = GetDpiForWindow(_windowHandle);
        if (dpi == 0)
        {
            dpi = DpiDefault;
        }

        int width = checked((int)Math.Round(BaseWidth * dpi / 96d));
        int height = checked((int)Math.Round(BaseHeight * dpi / 96d));
        RectInt32 workArea = GetWorkArea(cursor);
        int x = cursor.X - width + checked((int)Math.Round(12 * dpi / 96d));
        int y = cursor.Y - height - checked((int)Math.Round(8 * dpi / 96d));
        if (x < workArea.X)
        {
            x = cursor.X + checked((int)Math.Round(8 * dpi / 96d));
        }

        if (y < workArea.Y)
        {
            y = cursor.Y + checked((int)Math.Round(8 * dpi / 96d));
        }

        x = Math.Clamp(x, workArea.X, workArea.X +
            Math.Max(0, workArea.Width - width));
        y = Math.Clamp(y, workArea.Y, workArea.Y +
            Math.Max(0, workArea.Height - height));
        AppWindow.MoveAndResize(new(x, y, width, height));
    }

    private static RectInt32 GetWorkArea(Point point)
    {
        nint monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        MonitorInfo info = new()
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInfo>()),
        };
        return monitor != nint.Zero && GetMonitorInfo(monitor, ref info)
            ? info.WorkArea
            : new RectInt32(0, 0, 1920, 1080);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_isShown && !_disableInProgress &&
            args.WindowActivationState == WindowActivationState.Deactivated)
        {
            CloseMenu();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _isShown = false;
        Activated -= OnActivated;
        Closed -= OnClosed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void MenuRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_disableInProgress)
        {
            CloseMenu();
            e.Handled = true;
        }
    }

    private void CloseMenuButton_Click(object sender, RoutedEventArgs e) =>
        CloseMenu();

    private async void OptimizeMenuButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _optimize().ConfigureAwait(true);
    }

    private async void PauseMenuButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _togglePause().ConfigureAwait(true);
    }

    private void OpenMenuButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _open();
    }

    private void ExitMenuButton_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        _exit();
    }

    private void DisableMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButtonsPanel.Visibility = Visibility.Collapsed;
        DisablePromptPanel.Visibility = Visibility.Visible;
        ConfirmDisableButton.Focus(FocusState.Programmatic);
    }

    private void CancelDisableButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disableInProgress)
        {
            return;
        }

        DisablePromptPanel.Visibility = Visibility.Collapsed;
        ActionButtonsPanel.Visibility = Visibility.Visible;
        DisableStatusText.Text = string.Empty;
        DisableMenuButton.Focus(FocusState.Programmatic);
    }

    private async void ConfirmDisableButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_disableInProgress)
        {
            return;
        }

        _disableInProgress = true;
        CloseMenuButton.IsEnabled = false;
        ConfirmDisableButton.IsEnabled = false;
        CancelDisableButton.IsEnabled = false;
        DisableStatusText.Text =
            "Oczekiwanie na potwierdzenie UAC i zatrzymanie usługi…";
        try
        {
            if (await _disable().ConfigureAwait(true))
            {
                CloseMenu();
            }
            else
            {
                DisableStatusText.Text =
                    "Nie wyłączono komponentu. Usługa nadal działa.";
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or
                TimeoutException or OperationCanceledException or
                Win32Exception)
        {
            DisableStatusText.Text = exception.Message;
        }
        finally
        {
            _disableInProgress = false;
            if (!_isClosed)
            {
                CloseMenuButton.IsEnabled = true;
                ConfirmDisableButton.IsEnabled = true;
                CancelDisableButton.IsEnabled = true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal RectInt32 MonitorArea;
        internal RectInt32 WorkArea;
        internal uint Flags;
    }

#pragma warning disable SYSLIB1054
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, int size);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern nint MonitorFromPoint(
        Point point,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
#pragma warning restore SYSLIB1054
}

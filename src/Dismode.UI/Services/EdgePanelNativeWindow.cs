using System.ComponentModel;
using System.Runtime.InteropServices;
using Dismode.Core.Interaction;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Dismode.UI.Services;

internal sealed class EdgePanelNativeWindow : IDisposable
{
    private const uint TopmostPositionFlags = 0x0010 | 0x0020;
    private const uint TopmostZOrderFlags = 0x0010 | 0x0002 | 0x0001;
    private readonly nint _handle;
    private readonly nint _stripHandle;
    private readonly SubclassProc _procedure;
    private bool _disposed;
    private bool _trackingMain;
    private bool _trackingStrip;

    internal EdgePanelNativeWindow(Window window, bool roundCorners = true, bool enableHotStrip = false)
    {
        _handle = WindowNative.GetWindowHandle(window);
        try
        {
            window.AppWindow.IsShownInSwitchers = false;
        }
        catch (Exception exception) when (exception is NotImplementedException or COMException)
        {
            // Styl WS_EX_TOOLWINDOW ponizej ukrywa okno w Alt+Tab i na pasku zadan.
        }
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        window.SystemBackdrop = new TransparentBackdrop();
        // Popup bez ramki, narzedziowe i bez aktywacji takze po kliknieciu.
        nint style = GetWindowLongPtrW(_handle, -16);
        _ = SetWindowLongPtrW(_handle, -16,
            (style & ~(nint)0x00CF0000) | unchecked((nint)0x80000000));
        nint extended = GetWindowLongPtrW(_handle, -20) & ~(nint)0x00020301;
        _ = SetWindowLongPtrW(_handle, -20, extended | (nint)0x08000088);
        Margins glass = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        _ = DwmExtendFrameIntoClientArea(_handle, ref glass);
        int cornerPreference = roundCorners ? 2 : 1;
        _ = DwmSetWindowAttribute(_handle, 33, ref cornerPreference, sizeof(int));
        int borderColor = unchecked((int)0xFFFFFFFE);
        _ = DwmSetWindowAttribute(_handle, 34, ref borderColor, sizeof(int));
        _procedure = OnMessage;
        if (!SetWindowSubclass(_handle, _procedure, 1, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (enableHotStrip && Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") != "1") // PREVIEW-HACK
        {
            _stripHandle = CreateWindowExW(
                0x08080088,
                "STATIC",
                string.Empty,
                unchecked((int)0x80000000),
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0);
            if (_stripHandle != 0)
            {
                _ = SetLayeredWindowAttributes(_stripHandle, 0, 1, 2);
                _ = SetWindowSubclass(_stripHandle, _procedure, 2, 0);
            }
        }
    }

    internal event Action<int>? HotkeyPressed;
    internal event Action<bool>? PointerChanged;
    internal event Action? GeometryChanged;
    internal double Scale => GetDpiForWindow(_handle) / 96d;
    private DisplayArea CurrentDisplayArea => DisplayArea.GetFromWindowId(
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_handle), DisplayAreaFallback.Primary);
    internal RectInt32 WorkArea => CurrentDisplayArea.WorkArea;
    internal RectInt32 MonitorBounds => CurrentDisplayArea.OuterBounds;

    internal static EdgePanelRect ToEdgeRect(RectInt32 rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

    internal void Position(RectInt32 bounds) =>
        _ = SetWindowPos(_handle, new(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, TopmostPositionFlags);

    internal void PositionHotStrip(EdgePanelRect bounds)
    {
        if (_stripHandle != 0 && bounds.Width > 0 && bounds.Height > 0)
        {
            _ = SetWindowPos(_stripHandle, new(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, TopmostPositionFlags);
        }
    }

    internal void AssertTopmost()
    {
        if (_stripHandle != 0)
        {
            _ = SetWindowPos(_stripHandle, new(-1), 0, 0, 0, 0, TopmostZOrderFlags);
        }

        _ = SetWindowPos(_handle, new(-1), 0, 0, 0, 0, TopmostZOrderFlags);
    }

    internal void Show()
    {
        if (_stripHandle != 0)
        {
            _ = ShowWindow(_stripHandle, 4);
        }

        _ = ShowWindow(_handle, 4);
        AssertTopmost();
    }

    internal void Hide()
    {
        if (_stripHandle != 0)
        {
            _ = ShowWindow(_stripHandle, 0);
        }

        _ = ShowWindow(_handle, 0);
    }

    internal bool Register(int id, uint modifiers, uint key)
    {
        if (Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") == "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            return Environment.GetEnvironmentVariable("DISMODE_PREVIEW_HOTKEY_TAKEN") != "1"; // PREVIEW-HACK
        } // PREVIEW-HACK
        return RegisterHotKey(_handle, id, modifiers | 0x4000, key);
    }

    internal void Unregister(int id)
    {
        if (Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") == "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            return; // PREVIEW-HACK
        } // PREVIEW-HACK
        _ = UnregisterHotKey(_handle, id);
    }

    internal static bool PointerInsideHotZone(EdgePanelRect monitorBounds, EdgePanelRect handleBounds)
    {
        if (Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") == "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            return false; // PREVIEW-HACK
        } // PREVIEW-HACK
        return GetCursorPos(out NativePoint point)
            && EdgePanelMetrics.IsInHotZone(point.X, point.Y, monitorBounds, handleBounds);
    }

    internal static bool PointerInsideOpenPanel(
        EdgePanelRect cardBounds,
        EdgePanelRect monitorBounds,
        EdgePanelRect handleBounds,
        double scale,
        bool pointerEntered)
    {
        if (Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") == "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            return false; // PREVIEW-HACK
        } // PREVIEW-HACK
        return GetCursorPos(out NativePoint point)
            && EdgePanelMetrics.IsInOpenPanelZone(point.X, point.Y, cardBounds, monitorBounds, handleBounds, scale, pointerEntered);
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (window == _stripHandle && message == 0x0084)
        {
            return 1;
        }

        if (Environment.GetEnvironmentVariable("DISMODE_UI_PREVIEW") != "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            if (message == 0x0200)
            {
                bool isStrip = window == _stripHandle;
                if (isStrip ? !_trackingStrip : !_trackingMain)
                {
                    if (isStrip)
                    {
                        _trackingStrip = true;
                    }
                    else
                    {
                        _trackingMain = true;
                    }

                    TrackMouse tracking = new() { Size = (uint)Marshal.SizeOf<TrackMouse>(), Flags = 2, Window = window };
                    _ = TrackMouseEvent(ref tracking);
                }

                PointerChanged?.Invoke(true);
            }
            else if (message == 0x02A3)
            {
                if (window == _stripHandle)
                {
                    _trackingStrip = false;
                }
                else
                {
                    _trackingMain = false;
                }

                PointerChanged?.Invoke(false);
            }
        } // PREVIEW-HACK

        if (message is 0x02E0 or 0x007E or 0x001A)
        {
            GeometryChanged?.Invoke();
        }

        if (message == 0x0021)
        {
            return 3; // MA_NOACTIVATE: klik pozostaje w panelu, fokus zostaje w grze.
        }

        if (message == 0x0312)
        {
            HotkeyPressed?.Invoke((int)wParam);
            return 0;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_stripHandle != 0)
        {
            _ = RemoveWindowSubclass(_stripHandle, _procedure, 2);
            _ = DestroyWindow(_stripHandle);
        }

        _ = RemoveWindowSubclass(_handle, _procedure, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left; internal int Right; internal int Top; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouse { internal uint Size; internal uint Flags; internal nint Window; internal uint HoverTime; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouse tracking);
}

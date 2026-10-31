using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Dismode.UI.Services;

internal sealed class EdgePanelNativeWindow : IDisposable
{
    private readonly nint _handle;
    private readonly SubclassProc _procedure;
    private bool _disposed;
    private (int Width, int Height, int Left) _lastRegion;

    internal EdgePanelNativeWindow(Window window)
    {
        _handle = WindowNative.GetWindowHandle(window);
        window.AppWindow.IsShownInSwitchers = false;
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
        _procedure = OnMessage;
        if (!SetWindowSubclass(_handle, _procedure, 1, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal event Action<int>? HotkeyPressed;
    internal double Scale => GetDpiForWindow(_handle) / 96d;

    internal static RectInt32 PrimaryWorkArea => DisplayArea.Primary.WorkArea;
    internal static RectInt32 PrimaryBounds => DisplayArea.Primary.OuterBounds;

    internal void Position(RectInt32 bounds) =>
        _ = SetWindowPos(_handle, new(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0020);

    internal void Show() => _ = ShowWindow(_handle, 4);
    internal void Hide() => _ = ShowWindow(_handle, 0);

    internal bool Register(int id, uint modifiers, uint key) => RegisterHotKey(_handle, id, modifiers | 0x4000, key);
    internal void Unregister(int id) => _ = UnregisterHotKey(_handle, id);

    internal void Reveal(int width, int height, double amount)
    {
        // Obszar poza wysunieta czescia nie przechwytuje wejscia ani nie nachodzi na drugi monitor.
        int left = Math.Clamp((int)Math.Ceiling(width * (1 - amount)), 0, width);
        if (_lastRegion == (width, height, left))
        {
            return;
        }

        nint region = CreateRectRgn(left, 0, width, height);
        if (SetWindowRgn(_handle, region, true) == 0)
        {
            _ = DeleteObject(region);
        }
        else
        {
            _lastRegion = (width, height, left);
        }
    }

    internal static bool PointerInside(RectInt32 bounds)
    {
        return GetCursorPos(out NativePoint point)
            && point.X >= bounds.X && point.X < bounds.X + bounds.Width
            && point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;
    }

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
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
        _ = RemoveWindowSubclass(_handle, _procedure, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left; internal int Right; internal int Top; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { internal int X; internal int Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

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
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}

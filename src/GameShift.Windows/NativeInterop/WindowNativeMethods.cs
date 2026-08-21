using System.Runtime.InteropServices;
using System.Text;

namespace GameShift.Windows.NativeInterop;

internal static partial class WindowNativeMethods
{
    internal const uint GwOwner = 4;
    internal const uint WmClose = 0x0010;

    internal delegate int EnumWindowsProc(nint hWnd, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(
        EnumWindowsProc lpEnumFunc,
        nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(
        nint hWnd,
        out uint lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(
        nint hWnd,
        [Out] char[] lpClassName,
        int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(
        nint hWnd,
        uint msg,
        nint wParam,
        nint lParam);
}

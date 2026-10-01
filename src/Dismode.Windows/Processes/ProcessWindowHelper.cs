using System.Diagnostics;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Processes;

public static class ProcessWindowHelper
{
    private static readonly HashSet<string> IgnoredWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "UAC_InputIndicatorOverlayWnd",
        "UAC Input Indicator",
        "CiceroUIWndFrame",
        "CicLoaderWndClass",
        "MSCTFIME UI",
        "IME",
        "Touch Tooltip Window",
    };

    public static IReadOnlyList<nint> FindTopLevelWindows(int processId)
    {
        List<nint> windows = [];

        try
        {
            WindowNativeMethods.EnumWindows(
                (hWnd, lParam) =>
                {
                    _ = WindowNativeMethods.GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                    if (windowProcessId != (uint)processId)
                    {
                        return 1;
                    }

                    if (!WindowNativeMethods.IsWindowVisible(hWnd))
                    {
                        return 1;
                    }

                    nint owner = WindowNativeMethods.GetWindow(hWnd, WindowNativeMethods.GwOwner);
                    if (owner != IntPtr.Zero)
                    {
                        return 1;
                    }

                    char[] classNameChars = new char[256];
                    int length = WindowNativeMethods.GetClassName(hWnd, classNameChars, classNameChars.Length);
                    if (length > 0)
                    {
                        string className = new(classNameChars, 0, length);
                        if (IgnoredWindowClasses.Contains(className))
                        {
                            return 1;
                        }
                    }

                    windows.Add(hWnd);
                    return 1;
                },
                IntPtr.Zero);
        }
        catch
        {
            // Fallback gracefully if enumeration fails
        }

        return windows;
    }

    public static bool HasInteractiveWindow(Process process)
    {
        try
        {
            process.Refresh();
            IReadOnlyList<nint> windows = FindTopLevelWindows(process.Id);
            if (windows.Count > 0)
            {
                return true;
            }

            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestGracefulClose(Process process)
    {
        try
        {
            IReadOnlyList<nint> windows = FindTopLevelWindows(process.Id);
            bool posted = false;

            foreach (nint hWnd in windows)
            {
                if (WindowNativeMethods.PostMessageW(
                        hWnd,
                        WindowNativeMethods.WmClose,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    posted = true;
                }
            }

            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
            {
                if (process.CloseMainWindow())
                {
                    posted = true;
                }
            }

            return posted;
        }
        catch
        {
            return false;
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Display;

// API ekranu nalezy do SessionHost uzytkownika, nigdy do uslugi w sesji 0.
public sealed unsafe partial class WindowsDisplayApi : IDisplayApi
{
    public bool IsUserSession
    {
        get
        {
            using Process process = Process.GetCurrentProcess();
            return Environment.UserInteractive && process.SessionId != 0 && GetShellWindow() != 0;
        }
    }

    public string? FindGameMonitor(IReadOnlyList<int> processIds)
    {
        if (!IsUserSession)
        {
            return null;
        }
        nint foreground = GetForegroundWindow();
        nint selected = 0;
        long largestArea = 0;
        _ = WindowNativeMethods.EnumWindows((window, unused) =>
        {
            _ = GetWindowThreadProcessId(window, out uint pid);
            if (pid <= int.MaxValue && processIds.Contains((int)pid)
                && IsWindowVisible(window) && !IsIconic(window) && GetWindowRect(window, out WindowRect rectangle))
            {
                long width = (long)rectangle.Right - rectangle.Left;
                long height = (long)rectangle.Bottom - rectangle.Top;
                if (HasUsableWindowArea(width, height))
                {
                    if (window == foreground) { selected = window; return 0; }
                    if (width * height > largestArea) { selected = window; largestArea = width * height; }
                }
            }
            return 1;
        }, 0);
        return selected == 0 ? null : MonitorName(selected) ?? MonitorName(0);
    }

    internal static bool HasUsableWindowArea(long width, long height) => width >= 160 && height >= 120;

    public DisplayMode? GetCurrentMode(string deviceName)
    {
        if (!IsUserSession || !TryRead(deviceName, -1, out NativeMode mode)
            || DeviceId(deviceName) is not string id)
        {
            return null;
        }
        return ToMode(deviceName, id, mode);
    }

    public IReadOnlyList<DisplayMode> GetModes(string deviceName)
    {
        DisplayMode? current = GetCurrentMode(deviceName);
        if (current is null)
        {
            return [];
        }
        List<DisplayMode> modes = [];
        for (int index = 0; index < 4096 && TryRead(deviceName, index, out NativeMode mode); index++)
        {
            modes.Add(ToMode(deviceName, current.DeviceId, mode) with
            {
                PositionX = current.PositionX,
                PositionY = current.PositionY,
            });
        }
        return modes;
    }

    public void SetMode(DisplayMode mode)
    {
        DisplayMode? current = GetCurrentMode(mode.DeviceName);
        if (current is null || current with { RefreshRate = mode.RefreshRate } != mode
            || !TryRead(mode.DeviceName, -1, out NativeMode native))
        {
            throw new InvalidOperationException("Zmieniono monitor lub tryb ekranu.");
        }
        native.Fields = 0x00400000; // DM_DISPLAYFREQUENCY, bez zapisu w rejestrze.
        native.RefreshRate = mode.RefreshRate;
        int result = ChangeDisplaySettingsEx(mode.DeviceName, ref native, 0, 0, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"Nie zmieniono trybu ekranu (kod {result}).");
        }
    }

    private static bool TryRead(string device, int index, out NativeMode mode)
    {
        mode = new() { Size = 220 };
        return EnumDisplaySettings(device, index, ref mode);
    }

    private static DisplayMode ToMode(string name, string id, NativeMode mode) =>
        new(name, id, mode.Width, mode.Height, mode.BitsPerPixel, mode.RefreshRate,
            mode.Orientation, mode.PositionX, mode.PositionY, mode.DisplayFlags, mode.FixedOutput);

    private static string? MonitorName(nint window)
    {
        NativeMonitorInfo info = new() { Size = 104 };
        return GetMonitorInfo(MonitorFromWindow(window, window == 0 ? 1u : 0u), ref info)
            ? new string(info.DeviceName) : null;
    }

    private static string? DeviceId(string name)
    {
        NativeDisplayDevice device = new() { Size = 840 };
        NativeDisplayDevice second = new() { Size = 840 };
        if (EnumDisplayDevices(name, 1, ref second, 1))
        {
            return null;
        }
        string? id = EnumDisplayDevices(name, 0, ref device, 1)
            ? new string(device.DeviceId) : null;
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct NativeMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(72)] internal uint Fields;
        [FieldOffset(76)] internal int PositionX;
        [FieldOffset(80)] internal int PositionY;
        [FieldOffset(84)] internal int Orientation;
        [FieldOffset(88)] internal int FixedOutput;
        [FieldOffset(168)] internal int BitsPerPixel;
        [FieldOffset(172)] internal int Width;
        [FieldOffset(176)] internal int Height;
        [FieldOffset(180)] internal int DisplayFlags;
        [FieldOffset(184)] internal int RefreshRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out WindowRect rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [StructLayout(LayoutKind.Explicit, Size = 104)]
    private struct NativeMonitorInfo
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(40)] internal fixed char DeviceName[32];
    }

    [StructLayout(LayoutKind.Explicit, Size = 840)]
    private struct NativeDisplayDevice
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(328)] internal fixed char DeviceId[128];
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string deviceName, int mode, ref NativeMode settings);

    [LibraryImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ChangeDisplaySettingsEx(string deviceName, ref NativeMode settings,
        nint window, uint flags, nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(string deviceName, uint index, ref NativeDisplayDevice device, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();
}

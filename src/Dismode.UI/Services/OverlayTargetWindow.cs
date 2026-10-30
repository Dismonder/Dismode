using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

internal sealed class OverlayTargetWindow
{
    private readonly Func<int, nint> _resolve;
    private readonly Func<nint, int, bool> _isOwnedWindow;
    private readonly TimeProvider _clock;
    private int _processId;
    private nint _window;
    private DateTimeOffset _nextProbe;

    internal OverlayTargetWindow(
        Func<int, nint>? resolve = null,
        Func<nint, int, bool>? isOwnedWindow = null,
        TimeProvider? clock = null)
    {
        _resolve = resolve ?? ResolveProcessWindow;
        _isOwnedWindow = isOwnedWindow ?? IsOwnedWindow;
        _clock = clock ?? TimeProvider.System;
    }

    internal nint Resolve(int processId)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (_processId == processId && now < _nextProbe
            && (_window == nint.Zero || _isOwnedWindow(_window, processId)))
        {
            return _window;
        }

        _processId = processId;
        _window = _resolve(processId);
        _nextProbe = now.AddSeconds(1);
        return _window;
    }

    private static nint ResolveProcessWindow(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited ? nint.Zero : process.MainWindowHandle;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException or NotSupportedException or Win32Exception)
        {
            return nint.Zero;
        }
    }

    private static bool IsOwnedWindow(nint window, int processId)
    {
        if (!IsWindow(window))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out uint owner);
        return owner == (uint)processId;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

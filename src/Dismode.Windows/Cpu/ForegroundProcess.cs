using System.Runtime.InteropServices;

namespace Dismode.Windows.Cpu;

/// <summary>
/// The process that owns the window the user is looking at.
/// </summary>
public static partial class ForegroundProcess
{
    /// <summary>
    /// Zero when there is no foreground window or the caller cannot see one —
    /// a process in another session or window station gets nothing back, and
    /// "nobody is foreground" is the safe reading: the restraint loop then
    /// behaves as it did before the foreground exclusion existed.
    /// </summary>
    public static int GetProcessId()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId > int.MaxValue ? 0 : (int)processId;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}

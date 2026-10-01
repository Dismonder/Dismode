using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dismode.Windows.Processes;

/// <summary>
/// Reads a process executable path through QueryFullProcessImageName.
/// Process.MainModule enumerates every module of the target and needs
/// PROCESS_VM_READ, so it throws Access Denied for DRM-protected games and
/// anything running at higher integrity — exactly the processes Dismode
/// has to recognise. This asks only for PROCESS_QUERY_LIMITED_INFORMATION,
/// which Windows grants for processes the user owns.
/// </summary>
/// <remarks>
/// Public because the unprivileged UI scans for running library games with
/// the same predicate the elevated host uses to attach: a path it cannot
/// read is a game it must not claim to have found.
/// </remarks>
public static partial class ProcessImagePath
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumWindowsPath = 32767;

    public static string? TryRead(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return TryRead(process.Id);
    }

    public static unsafe string? TryRead(int processId)
    {
        nint handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle == nint.Zero)
        {
            return null;
        }

        try
        {
            char[] path = new char[MaximumWindowsPath];
            uint length = checked((uint)path.Length);
            fixed (char* pathPointer = path)
            {
                return QueryFullProcessImageName(
                    handle,
                    flags: 0,
                    pathPointer,
                    ref length)
                    ? new string(pathPointer, 0, checked((int)length))
                    : null;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        nint processHandle,
        uint flags,
        char* executablePath,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

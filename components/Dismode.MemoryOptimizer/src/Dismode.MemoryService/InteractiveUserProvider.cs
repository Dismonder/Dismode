using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Dismode.MemoryService;

internal sealed partial class InteractiveUserProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    internal static IReadOnlyList<string> GetInteractiveUserSids()
    {
        HashSet<string> sids = new(StringComparer.OrdinalIgnoreCase);
        if (Environment.UserInteractive)
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            if (current.User?.Value is { Length: > 0 } currentSid)
            {
                sids.Add(currentSid);
            }
        }

        foreach (Process process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    using SafeProcessHandle processHandle = OpenProcess(
                        ProcessQueryLimitedInformation,
                        inheritHandle: false,
                        process.Id);
                    if (processHandle.IsInvalid ||
                        !OpenProcessToken(
                            processHandle,
                            TokenQuery,
                            out SafeAccessTokenHandle token))
                    {
                        continue;
                    }

                    using (token)
                    using (WindowsIdentity identity = new(
                        token.DangerousGetHandle()))
                    {
                        if (identity.User?.Value is { Length: > 0 } sid)
                        {
                            sids.Add(sid);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception)
                {
                    Trace.TraceInformation(
                        "Skipped an Explorer process during user discovery: {0}",
                        exception.Message);
                }
            }
        }

        return sids.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);
}

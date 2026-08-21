using System.ComponentModel;
using System.Runtime.InteropServices;
using GameShift.Windows.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.Processes;

public sealed class ProcessParentMapProvider : IProcessParentMapProvider
{
    private const int ErrorNoMoreFiles = 18;

    public unsafe IReadOnlyDictionary<int, int> Capture()
    {
        using SafeFileHandle snapshot =
            ProcessNativeMethods.CreateToolhelp32Snapshot(
                ToolhelpSnapshotFlags.Process,
                processId: 0);
        if (snapshot.IsInvalid)
        {
            throw new Win32Exception();
        }

        ProcessEntry32 entry = new()
        {
            Size = checked((uint)sizeof(ProcessEntry32)),
        };
        Dictionary<int, int> parents = [];

        if (ProcessNativeMethods.Process32First(snapshot, ref entry) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == ErrorNoMoreFiles
                ? parents
                : throw new Win32Exception(error);
        }

        while (true)
        {
            if (entry.ProcessId is > 0 and <= int.MaxValue
                && entry.ParentProcessId <= int.MaxValue)
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }

            entry.Size = checked((uint)sizeof(ProcessEntry32));
            if (ProcessNativeMethods.Process32Next(snapshot, ref entry) != 0)
            {
                continue;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoMoreFiles)
            {
                break;
            }

            throw new Win32Exception(error);
        }

        return parents;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

internal static class ProcessPowerThrottlingController
{
    internal static ProcessEcoQosState Read(Process process)
    {
        ProcessPowerThrottlingState state = new()
        {
            Version =
                ProcessNativeMethods.ProcessPowerThrottlingCurrentVersion,
        };

        if (ProcessNativeMethods.GetProcessInformation(
                process.SafeHandle,
                ProcessInformationClass.ProcessPowerThrottling,
                ref state,
                checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>())) == 0)
        {
            throw new Win32Exception();
        }

        bool executionSpeedThrottled =
            (state.StateMask
                & ProcessNativeMethods.ProcessPowerThrottlingExecutionSpeed) != 0;
        return new(executionSpeedThrottled);
    }

    internal static void Set(Process process, bool enabled)
    {
        ProcessPowerThrottlingState state = new()
        {
            Version =
                ProcessNativeMethods.ProcessPowerThrottlingCurrentVersion,
            ControlMask =
                ProcessNativeMethods.ProcessPowerThrottlingExecutionSpeed,
            StateMask = enabled
                ? ProcessNativeMethods.ProcessPowerThrottlingExecutionSpeed
                : 0,
        };

        if (ProcessNativeMethods.SetProcessInformation(
                process.SafeHandle,
                ProcessInformationClass.ProcessPowerThrottling,
                in state,
                checked((uint)Marshal.SizeOf<ProcessPowerThrottlingState>())) == 0)
        {
            throw new Win32Exception();
        }
    }
}

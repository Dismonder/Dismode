using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Processes;

internal static class ProcessPowerThrottlingController
{
    internal static ProcessEcoQosState Read(Process process)
        => new(ReadMode(ReadNative(process)));

    private static ProcessPowerThrottlingState ReadNative(Process process)
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

        return state;
    }

    internal static EcoQosMode ReadMode(ProcessPowerThrottlingState state)
    {
        const uint executionSpeed = ProcessNativeMethods.ProcessPowerThrottlingExecutionSpeed;
        return (state.ControlMask & executionSpeed) == 0
            ? EcoQosMode.SystemManaged
            : (state.StateMask & executionSpeed) != 0 ? EcoQosMode.On : EcoQosMode.Off;
    }

    internal static ProcessPowerThrottlingState WithMode(
        ProcessPowerThrottlingState state,
        EcoQosMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        const uint executionSpeed = ProcessNativeMethods.ProcessPowerThrottlingExecutionSpeed;
        state.ControlMask = (state.ControlMask & ~executionSpeed)
            | (mode == EcoQosMode.SystemManaged ? 0u : executionSpeed);
        state.StateMask = (state.StateMask & ~executionSpeed)
            | (mode == EcoQosMode.On ? executionSpeed : 0u);
        return state;
    }

    internal static void Set(Process process, EcoQosMode mode, EcoQosMode expectedMode)
    {
        ProcessPowerThrottlingState original = ReadNative(process);
        EcoQosMode currentMode = ReadMode(original);
        if (currentMode == mode)
        {
            return;
        }

        if (currentMode != expectedMode)
        {
            throw new InvalidOperationException(
                "Wykryto zewnętrzną zmianę trybu EcoQoS; zachowano bieżący stan.");
        }

        // Modyfikujemy tylko ExecutionSpeed; pozostale bity naleza do innych ustawien.
        ProcessPowerThrottlingState state = WithMode(original, mode);
        ProcessPowerThrottlingState latest = ReadNative(process);
        if (latest.ControlMask != original.ControlMask || latest.StateMask != original.StateMask)
        {
            throw new InvalidOperationException(
                "Stan ograniczania mocy procesu zmienił się przed zapisem EcoQoS.");
        }

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

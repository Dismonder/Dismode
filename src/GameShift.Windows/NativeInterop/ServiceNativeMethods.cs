using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.NativeInterop;

internal static partial class ServiceNativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceConfigDelayedAutoStartInfo = 3;
    internal const uint ServiceConfigTriggerInfo = 8;
    internal const int ScStatusProcessInfo = 0;

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "OpenSCManagerW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenScManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "OpenServiceW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "QueryServiceConfigW",
        SetLastError = true)]
    internal static partial int QueryServiceConfig(
        SafeServiceHandle service,
        nint serviceConfig,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "QueryServiceConfig2W",
        SetLastError = true)]
    internal static partial int QueryServiceConfig2(
        SafeServiceHandle service,
        uint infoLevel,
        nint buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "QueryServiceStatusEx",
        SetLastError = true)]
    internal static partial int QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint serviceHandle);
}

internal sealed class SafeServiceHandle :
    SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeServiceHandle()
        : base(ownsHandle: true)
    {
    }

    internal SafeServiceHandle(nint serviceHandle)
        : base(ownsHandle: true)
    {
        SetHandle(serviceHandle);
    }

    protected override bool ReleaseHandle() =>
        ServiceNativeMethods.CloseServiceHandle(handle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct QueryServiceConfigNative
{
    internal uint ServiceType;
    internal uint StartType;
    internal uint ErrorControl;
    internal nint BinaryPathName;
    internal nint LoadOrderGroup;
    internal uint TagId;
    internal nint Dependencies;
    internal nint ServiceStartName;
    internal nint DisplayName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceDelayedAutoStartInfo
{
    internal int IsDelayedAutoStart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceTriggerInfo
{
    internal uint TriggerCount;
    internal nint Triggers;
    internal nint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceStatusProcess
{
    internal uint ServiceType;
    internal uint CurrentState;
    internal uint ControlsAccepted;
    internal uint Win32ExitCode;
    internal uint ServiceSpecificExitCode;
    internal uint CheckPoint;
    internal uint WaitHint;
    internal uint ProcessId;
    internal uint ServiceFlags;
}

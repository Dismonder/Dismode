using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace GameShift.Windows.NativeInterop;

internal static partial class ProcessNativeMethods
{
    internal const uint ProcessPowerThrottlingCurrentVersion = 1;
    internal const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    /// <summary>
    /// MEMORY_PRIORITY_INFORMATION values. Five is the default for every
    /// user process and the highest a process can be given through this
    /// call; one is what Windows itself assigns to its own background work
    /// (indexing, prefetch), so the memory manager repurposes those pages
    /// before anyone else's when physical memory runs short.
    /// </summary>
    internal const uint MemoryPriorityVeryLow = 1;
    internal const uint MemoryPriorityLow = 2;
    internal const uint MemoryPriorityMedium = 3;
    internal const uint MemoryPriorityBelowNormal = 4;
    internal const uint MemoryPriorityNormal = 5;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    internal static partial int OpenProcessToken(
        SafeProcessHandle processHandle,
        TokenAccessLevels desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetProcessInformation(
        SafeProcessHandle processHandle,
        ProcessInformationClass processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int SetProcessInformation(
        SafeProcessHandle processHandle,
        ProcessInformationClass processInformationClass,
        in ProcessPowerThrottlingState processInformation,
        uint processInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GetProcessInformation(
        SafeProcessHandle processHandle,
        ProcessInformationClass processInformationClass,
        ref MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int SetProcessInformation(
        SafeProcessHandle processHandle,
        ProcessInformationClass processInformationClass,
        in MemoryPriorityInformation processInformation,
        uint processInformationSize);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        char* executablePath,
        ref uint size);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateToolhelp32Snapshot",
        SetLastError = true)]
    internal static partial SafeFileHandle CreateToolhelp32Snapshot(
        ToolhelpSnapshotFlags flags,
        uint processId);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "Process32FirstW",
        SetLastError = true)]
    internal static unsafe partial int Process32First(
        SafeFileHandle snapshot,
        ref ProcessEntry32 entry);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "Process32NextW",
        SetLastError = true)]
    internal static unsafe partial int Process32Next(
        SafeFileHandle snapshot,
        ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32EmptyWorkingSet(SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessWorkingSetSize(
        SafeProcessHandle processHandle,
        nuint minimumWorkingSetSize,
        nuint maximumWorkingSetSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSystemFileCacheSize(
        nint minimumFileCacheSize,
        nint maximumFileCacheSize,
        uint flags);
}

internal enum ProcessInformationClass
{
    ProcessMemoryPriority = 0,
    ProcessPowerThrottling = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessPowerThrottlingState
{
    internal uint Version;
    internal uint ControlMask;
    internal uint StateMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryPriorityInformation
{
    internal uint MemoryPriority;
}

[Flags]
internal enum ToolhelpSnapshotFlags : uint
{
    Process = 0x00000002,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ProcessEntry32
{
    internal uint Size;
    internal uint UsageCount;
    internal uint ProcessId;
    internal nuint DefaultHeapId;
    internal uint ModuleId;
    internal uint ThreadCount;
    internal uint ParentProcessId;
    internal int BasePriority;
    internal uint Flags;
    internal fixed char ExecutableFile[260];
}

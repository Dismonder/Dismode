// SPDX-License-Identifier: GPL-3.0-only
// Derived from Windows Memory Cleaner 3.0.8 © Igor Mundstein.
// Modified for .NET 10/x64, scoped privileges, SafeHandle and NTSTATUS mapping.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.MemoryOptimizer.Core.Native;

internal static partial class NativeMethods
{
    internal const int ErrorSuccess = 0;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorNotAllAssigned = 1300;

    internal const uint TokenQuery = 0x0008;
    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint SePrivilegeEnabled = 0x00000002;

    internal const uint ProcessSetQuota = 0x0100;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    internal const int SystemFileCacheInformation = 21;
    internal const int SystemMemoryListInformation = 80;
    internal const int SystemCombinePhysicalMemoryInformation = 130;
    internal const int SystemRegistryReconciliationInformation = 155;

    internal const int MemoryEmptyWorkingSets = 2;
    internal const int MemoryFlushModifiedList = 3;
    internal const int MemoryPurgeStandbyList = 4;
    internal const int MemoryPurgeLowPriorityStandbyList = 5;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileFlagNoBuffering = 0x20000000;
    internal const uint FsctlDiscardVolumeCache = 589828;
    internal const uint FsctlResetWriteOrder = 589832;

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport(
        "advapi32.dll",
        EntryPoint = "LookupPrivilegeValueW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(
        string? systemName,
        string privilegeName,
        out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        SafeAccessTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        out TokenPrivileges previousState,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32EmptyWorkingSet(
        SafeProcessHandle processHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(
        ref MemoryStatusEx buffer);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationInt(
        int systemInformationClass,
        ref int systemInformation,
        uint systemInformationLength);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationCombine(
        int systemInformationClass,
        ref MemoryCombineInformationEx systemInformation,
        uint systemInformationLength);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationFileCache(
        int systemInformationClass,
        ref SystemFileCacheInformation systemInformation,
        uint systemInformationLength);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
    internal static partial int NtSetSystemInformationEmpty(
        int systemInformationClass,
        nint systemInformation,
        uint systemInformationLength);

    [LibraryImport("ntdll.dll")]
    internal static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetSystemFileCacheSize(
        nuint minimumFileCacheSize,
        nuint maximumFileCacheSize,
        uint flags);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        nint inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle fileHandle);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    internal uint LowPart;
    internal int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LuidAndAttributes
{
    internal Luid Luid;
    internal uint Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    internal uint PrivilegeCount;
    internal LuidAndAttributes Privileges;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    internal uint Length;
    internal uint MemoryLoad;
    internal ulong TotalPhysical;
    internal ulong AvailablePhysical;
    internal ulong TotalPageFile;
    internal ulong AvailablePageFile;
    internal ulong TotalVirtual;
    internal ulong AvailableVirtual;
    internal ulong AvailableExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryCombineInformationEx
{
    internal nint Handle;
    internal nuint PagesCombined;
    internal ulong Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemFileCacheInformation
{
    internal nuint CurrentSize;
    internal nuint PeakSize;
    internal uint PageFaultCount;
    internal nuint MinimumWorkingSet;
    internal nuint MaximumWorkingSet;
    internal nuint CurrentSizeIncludingTransitionInPages;
    internal nuint PeakSizeIncludingTransitionInPages;
    internal uint TransitionRePurposeCount;
    internal uint Flags;
}

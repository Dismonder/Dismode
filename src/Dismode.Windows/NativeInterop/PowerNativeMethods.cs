using System.Runtime.InteropServices;

namespace Dismode.Windows.NativeInterop;

internal static partial class PowerNativeMethods
{
    internal const uint AccessScheme = 16;
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorNoMoreItems = 259;

    [LibraryImport("PowrProf.dll")]
    internal static partial uint PowerGetActiveScheme(
        nint userRootPowerKey,
        out nint activePolicyGuid);

    [LibraryImport("PowrProf.dll")]
    internal static partial uint PowerSetActiveScheme(
        nint userRootPowerKey,
        in Guid schemeGuid);

    [LibraryImport("PowrProf.dll")]
    internal static partial uint PowerDuplicateScheme(
        nint rootPowerKey,
        in Guid sourceSchemeGuid,
        ref nint destinationSchemeGuid);

    [LibraryImport("PowrProf.dll")]
    internal static partial uint PowerDeleteScheme(
        nint rootPowerKey,
        in Guid schemeGuid);

    [LibraryImport("PowrProf.dll")]
    internal static partial uint PowerEnumerate(
        nint rootPowerKey,
        nint schemeGuid,
        nint subGroupOfPowerSettingsGuid,
        uint accessFlags,
        uint index,
        nint buffer,
        ref uint bufferSize);

    [LibraryImport("Kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(
        out SystemPowerStatus systemPowerStatus);

    internal const uint PowerRequestContextVersion = 0;
    internal const uint PowerRequestContextSimpleString = 1;

    /// <summary>POWER_REQUEST_TYPE: ekran nie gasnie.</summary>
    internal const int PowerRequestDisplayRequired = 0;

    /// <summary>POWER_REQUEST_TYPE: system nie zasypia.</summary>
    internal const int PowerRequestSystemRequired = 1;

    /// <summary>Zwraca INVALID_HANDLE_VALUE (-1) przy bledzie.</summary>
    [LibraryImport("Kernel32.dll", SetLastError = true)]
    internal static partial nint PowerCreateRequest(
        ref PowerReasonContext context);

    [LibraryImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerSetRequest(
        nint powerRequest,
        int requestType);

    [LibraryImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerClearRequest(
        nint powerRequest,
        int requestType);

    [LibraryImport("Kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}

/// <summary>
/// REASON_CONTEXT w wariancie z prostym napisem. Unia w systemie ma 24 bajty
/// (wariant szczegolowy), wiec rozmiar struktury zostaje pelny.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PowerReasonContext
{
    internal uint Version;
    internal uint Flags;
    internal nint SimpleReasonString;
    internal nint Reserved1;
    internal nint Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    internal byte ACLineStatus;
    internal byte BatteryFlag;
    internal byte BatteryLifePercent;
    internal byte SystemStatusFlag;
    internal int BatteryLifeTime;
    internal int BatteryFullLifeTime;
}


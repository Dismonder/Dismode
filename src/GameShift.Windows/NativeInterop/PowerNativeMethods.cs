using System.Runtime.InteropServices;

namespace GameShift.Windows.NativeInterop;

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


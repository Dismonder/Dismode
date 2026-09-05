using System.Runtime.InteropServices;

namespace GameShift.Windows.SystemOptimization;

public static partial class SystemMemoryUsageReader
{
    public static long? TryReadUsedPhysicalMemoryBytes()
    {
        MemoryStatus status = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatus>()),
        };
        return GlobalMemoryStatusEx(ref status)
            ? MapUsedPhysicalMemoryBytes(
                status.TotalPhysical,
                status.AvailablePhysical)
            : null;
    }

    internal static long? MapUsedPhysicalMemoryBytes(
        ulong totalPhysicalBytes,
        ulong availablePhysicalBytes)
    {
        if (availablePhysicalBytes > totalPhysicalBytes)
        {
            return null;
        }

        ulong used = totalPhysicalBytes - availablePhysicalBytes;
        return used <= long.MaxValue ? checked((long)used) : null;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GlobalMemoryStatusEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
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
}

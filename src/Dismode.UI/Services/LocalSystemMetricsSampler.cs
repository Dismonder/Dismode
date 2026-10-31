using System.Runtime.InteropServices;
using Dismode.Windows.Cpu;

namespace Dismode.UI.Services;

internal sealed class LocalSystemMetricsSampler
{
    private readonly SystemCpuLoadSampler _cpu = new(clampExcessIdle: true);

    public LocalSystemMetricsSnapshot Sample()
    {
        double? cpuPercent = _cpu.Sample();
        (ulong? usedMemory, ulong? totalMemory) = SampleMemory();
        double? systemDriveUsedPercent = SampleSystemDriveUsedPercent();
        return new(
            cpuPercent,
            usedMemory,
            totalMemory,
            systemDriveUsedPercent);
    }

    private static (ulong? UsedMemory, ulong? TotalMemory) SampleMemory()
    {
        NativeMemoryStatus status = new()
        {
            Length = (uint)Marshal.SizeOf<NativeMemoryStatus>(),
        };
        if (!GlobalMemoryStatusEx(ref status)
            || status.TotalPhysical == 0)
        {
            return (null, null);
        }

        ulong used = status.TotalPhysical
            - Math.Min(status.AvailablePhysical, status.TotalPhysical);
        return (used, status.TotalPhysical);
    }

    private static double? SampleSystemDriveUsedPercent()
    {
        try
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            DriveInfo drive = new(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return null;
            }

            return Math.Clamp(
                (drive.TotalSize - drive.AvailableFreeSpace)
                    * 100d
                    / drive.TotalSize,
                0,
                100);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(
        ref NativeMemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

internal sealed record LocalSystemMetricsSnapshot(
    double? CpuPercent,
    ulong? UsedPhysicalMemoryBytes,
    ulong? TotalPhysicalMemoryBytes,
    double? SystemDriveUsedPercent);

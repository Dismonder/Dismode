using System.Runtime.InteropServices;

namespace Dismode.UI.Services;

internal sealed class LocalSystemMetricsSampler
{
    private ulong? _lastIdleTime;
    private ulong? _lastKernelTime;
    private ulong? _lastUserTime;

    public LocalSystemMetricsSnapshot Sample()
    {
        double? cpuPercent = SampleCpuPercent();
        (ulong? usedMemory, ulong? totalMemory) = SampleMemory();
        double? systemDriveUsedPercent = SampleSystemDriveUsedPercent();
        return new(
            cpuPercent,
            usedMemory,
            totalMemory,
            systemDriveUsedPercent);
    }

    private double? SampleCpuPercent()
    {
        if (!GetSystemTimes(
                out NativeFileTime idle,
                out NativeFileTime kernel,
                out NativeFileTime user))
        {
            return null;
        }

        ulong idleTime = idle.ToUInt64();
        ulong kernelTime = kernel.ToUInt64();
        ulong userTime = user.ToUInt64();
        double? result = null;
        if (_lastIdleTime is ulong previousIdle
            && _lastKernelTime is ulong previousKernel
            && _lastUserTime is ulong previousUser
            && idleTime >= previousIdle
            && kernelTime >= previousKernel
            && userTime >= previousUser)
        {
            ulong idleDelta = idleTime - previousIdle;
            ulong totalDelta = kernelTime - previousKernel
                + userTime - previousUser;
            if (totalDelta > 0)
            {
                result = Math.Clamp(
                    (totalDelta - Math.Min(idleDelta, totalDelta))
                        * 100d
                        / totalDelta,
                    0,
                    100);
            }
        }

        _lastIdleTime = idleTime;
        _lastKernelTime = kernelTime;
        _lastUserTime = userTime;
        return result;
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
    private static extern bool GetSystemTimes(
        out NativeFileTime idleTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(
        ref NativeMemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() =>
            ((ulong)High << 32) | Low;
    }

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

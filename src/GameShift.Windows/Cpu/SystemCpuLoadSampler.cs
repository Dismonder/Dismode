using System.Runtime.InteropServices;

namespace GameShift.Windows.Cpu;

/// <summary>
/// Machine-wide CPU load, sampled as the change between two calls.
/// <para>
/// The first call has nothing to compare against and reports null rather than
/// zero: a caller that treated "no reading yet" as "idle" would decide the
/// machine is quiet at the exact moment a session starts.
/// </para>
/// </summary>
public sealed partial class SystemCpuLoadSampler
{
    private ulong? _lastIdle;
    private ulong? _lastKernel;
    private ulong? _lastUser;

    public double? Sample()
    {
        if (!GetSystemTimes(
                out FileTime idle,
                out FileTime kernel,
                out FileTime user))
        {
            return null;
        }

        ulong idleTime = idle.ToUInt64();
        ulong kernelTime = kernel.ToUInt64();
        ulong userTime = user.ToUInt64();

        double? percent = null;
        if (_lastIdle is ulong previousIdle
            && _lastKernel is ulong previousKernel
            && _lastUser is ulong previousUser
            && idleTime >= previousIdle
            && kernelTime >= previousKernel
            && userTime >= previousUser)
        {
            // Kernel time already includes idle time, so the busy share is
            // the total minus idle rather than kernel plus user.
            ulong total = kernelTime - previousKernel + (userTime - previousUser);
            ulong idleDelta = idleTime - previousIdle;
            if (total > 0 && total >= idleDelta)
            {
                percent = Math.Clamp(
                    100.0 * (total - idleDelta) / total,
                    0,
                    100);
            }
        }

        _lastIdle = idleTime;
        _lastKernel = kernelTime;
        _lastUser = userTime;
        return percent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }
}

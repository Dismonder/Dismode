using System.Runtime.InteropServices;

namespace Dismode.Windows.Cpu;

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
    private readonly bool _clampExcessIdle;
    private readonly Func<ProcessorTimes?> _read;
    private ulong? _lastIdle;
    private ulong? _lastKernel;
    private ulong? _lastUser;

    public SystemCpuLoadSampler() : this(false, ReadTimes) { }

    public SystemCpuLoadSampler(bool clampExcessIdle) : this(clampExcessIdle, ReadTimes) { }

    internal SystemCpuLoadSampler(bool clampExcessIdle, Func<ProcessorTimes?> read)
    {
        _clampExcessIdle = clampExcessIdle;
        _read = read;
    }

    public double? Sample()
    {
        if (_read() is not ProcessorTimes times)
        {
            return null;
        }

        ulong idleTime = times.Idle;
        ulong kernelTime = times.Kernel;
        ulong userTime = times.User;

        double? percent = null;
        if (_lastIdle is ulong previousIdle
            && _lastKernel is ulong previousKernel
            && _lastUser is ulong previousUser
            && ProcessorTimes.TryGetDelta(
                new(previousIdle, previousKernel, previousUser),
                new(idleTime, kernelTime, userTime), out double total, out double busy, _clampExcessIdle)
            && total > 0)
        {
            percent = 100d * busy / total;
        }

        _lastIdle = idleTime;
        _lastKernel = kernelTime;
        _lastUser = userTime;
        return percent;
    }

    private static ProcessorTimes? ReadTimes() => GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user)
        ? new(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64())
        : null;

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

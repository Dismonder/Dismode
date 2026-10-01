using System.Runtime.InteropServices;

namespace Dismode.MemoryService;

internal sealed partial class SystemCpuSampler
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _initialized;

    internal double Sample()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            return 100;
        }

        ulong idleValue = idle.ToUInt64();
        ulong kernelValue = kernel.ToUInt64();
        ulong userValue = user.ToUInt64();
        if (!_initialized)
        {
            _initialized = true;
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            return 100;
        }

        ulong idleDelta = idleValue - _previousIdle;
        ulong kernelDelta = kernelValue - _previousKernel;
        ulong userDelta = userValue - _previousUser;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;

        ulong total = kernelDelta + userDelta;
        if (total == 0 || idleDelta > total)
        {
            return 100;
        }

        return Math.Clamp((total - idleDelta) * 100d / total, 0, 100);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        internal ulong ToUInt64() => ((ulong)_high << 32) | _low;
    }
}

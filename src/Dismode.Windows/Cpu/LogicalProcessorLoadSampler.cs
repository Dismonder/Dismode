using System.Runtime.InteropServices;

namespace Dismode.Windows.Cpu;

public readonly record struct LogicalProcessorLoad(double CpuPercent, double BusiestProcessorPercent);

internal readonly record struct ProcessorTimes(ulong Idle, ulong Kernel, ulong User)
{
    internal static bool TryGetDelta(ProcessorTimes previous, ProcessorTimes current,
        out double total, out double busy, bool clampExcessIdle = false)
    {
        total = busy = 0;
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
        {
            return false;
        }

        // Czas jadra zawiera idle. Dodawanie jako double zapobiega przepelnieniu sumy.
        total = (double)(current.Kernel - previous.Kernel) + (current.User - previous.User);
        double idle = current.Idle - previous.Idle;
        busy = Math.Max(0, total - idle);
        return clampExcessIdle || idle <= total;
    }
}

internal interface ILogicalProcessorTimeSource
{
    ushort GroupCount { get; }
    bool TryRead(Span<ProcessorTimes> destination, out int count);
}

public sealed class LogicalProcessorLoadSampler
{
    private readonly ILogicalProcessorTimeSource _source;
    private readonly ProcessorTimes[] _current = new ProcessorTimes[64];
    private readonly ProcessorTimes[] _previous = new ProcessorTimes[64];
    private readonly double[] _loads = new double[64];
    private int _count;
    private int _validCount;

    public LogicalProcessorLoadSampler() : this(new NativeProcessorTimeSource()) { }

    internal LogicalProcessorLoadSampler(ILogicalProcessorTimeSource source) => _source = source;

    // Widok wazny do kolejnego Sample lub Reset; bez kopii tablicy przy kazdym odczycie.
    public ReadOnlySpan<double> ProcessorLoads => _loads.AsSpan(0, _validCount);

    public void Reset() => _count = _validCount = 0;

    public LogicalProcessorLoad? Sample()
    {
        _validCount = 0;
        if (_source.GroupCount != 1 || !_source.TryRead(_current, out int count) || count is <= 0 or > 64)
        {
            Reset();
            return null;
        }

        bool valid = _count == count;
        double total = 0;
        double busy = 0;
        double maximum = 0;
        for (int index = 0; valid && index < count; index++)
        {
            valid = ProcessorTimes.TryGetDelta(_previous[index], _current[index], out double delta, out double active);
            total += delta;
            busy += active;
            _loads[index] = delta > 0 ? 100d * active / delta : 0;
            maximum = Math.Max(maximum, _loads[index]);
        }

        bool shortWindow = total / count < TimeSpan.TicksPerMillisecond * 100;

        // Krotkie okna kumuluja czas; regresja lub zmiana topologii ustawia nowa baze.
        if (!valid || !shortWindow)
        {
            _current.AsSpan(0, count).CopyTo(_previous);
            _count = count;
        }

        if (!valid || shortWindow)
        {
            return null;
        }

        _validCount = count;
        return new(100d * busy / total, maximum);
    }
}

internal sealed partial class NativeProcessorTimeSource : ILogicalProcessorTimeSource
{
    private readonly ProcessorPerformanceInformation[] _buffer = new ProcessorPerformanceInformation[64];

    public ushort GroupCount => GetActiveProcessorGroupCount();

    public unsafe bool TryRead(Span<ProcessorTimes> destination, out int count)
    {
        fixed (ProcessorPerformanceInformation* buffer = _buffer)
        {
            int status = NtQuerySystemInformation(8, buffer,
                (uint)(_buffer.Length * sizeof(ProcessorPerformanceInformation)), out uint returned);
            count = (int)(returned / sizeof(ProcessorPerformanceInformation));
            if (status < 0 || returned % sizeof(ProcessorPerformanceInformation) != 0
                || count <= 0 || count > destination.Length)
            {
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                ProcessorPerformanceInformation value = _buffer[index];
                if (value.Idle < 0 || value.Kernel < 0 || value.User < 0)
                {
                    return false;
                }

                destination[index] = new((ulong)value.Idle, (ulong)value.Kernel, (ulong)value.User);
            }

            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformanceInformation
    {
        internal long Idle;
        internal long Kernel;
        internal long User;
        internal long Dpc;
        internal long Interrupt;
        internal uint InterruptCount;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int informationClass,
        void* buffer, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();
}

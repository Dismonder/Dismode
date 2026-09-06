using System.Runtime.InteropServices;
using GameShift.Core.Cpu;

namespace GameShift.Windows.Cpu;

/// <summary>
/// Reads the machine's CPU sets. This is the API that knows about performance
/// tiers: GetLogicalProcessorInformationEx describes cache and package layout
/// but says nothing about how fast a core is, and Win32_Processor reports one
/// row for the whole package. Only a CPU set carries EfficiencyClass per
/// logical processor, which is what separates P-cores from E-cores.
/// </summary>
public static partial class SystemCpuTopologyProvider
{
    private const uint CpuSetInformationType = 0;

    public static CpuTopology? Read()
    {
        uint required = 0;
        if (!GetSystemCpuSetInformation(
                nint.Zero,
                0,
                ref required,
                nint.Zero,
                0)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return null;
        }

        if (required == 0 || required > 1024 * 1024)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            uint returned = required;
            if (!GetSystemCpuSetInformation(
                    buffer,
                    required,
                    ref returned,
                    nint.Zero,
                    0))
            {
                return null;
            }

            return new(ReadProcessors(buffer, returned));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<CpuLogicalProcessor> ReadProcessors(
        nint buffer,
        uint length)
    {
        List<CpuLogicalProcessor> processors = [];
        uint offset = 0;
        while (offset + SystemCpuSetInformation.HeaderSize <= length)
        {
            SystemCpuSetInformation entry =
                Marshal.PtrToStructure<SystemCpuSetInformation>(
                    buffer + checked((int)offset));
            if (entry.Size == 0 || offset + entry.Size > length)
            {
                break;
            }

            if (entry.Type == CpuSetInformationType)
            {
                processors.Add(new(
                    entry.Id,
                    entry.Group,
                    entry.LogicalProcessorIndex,
                    entry.CoreIndex,
                    entry.EfficiencyClass,
                    (entry.AllFlags & ParkedFlag) != 0,
                    (entry.AllFlags & AllocatedFlag) != 0));
            }

            offset += entry.Size;
        }

        return processors;
    }

    private const int ErrorInsufficientBuffer = 122;
    private const byte ParkedFlag = 0x1;
    private const byte AllocatedFlag = 0x2;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(
        nint information,
        uint bufferLength,
        ref uint returnedLength,
        nint process,
        uint flags);

    /// <summary>
    /// SYSTEM_CPU_SET_INFORMATION. The union after the header is only ever the
    /// CpuSet variant today; Type is checked before the fields are trusted.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSetInformation
    {
        internal const uint HeaderSize = 8;

        internal uint Size;
        internal uint Type;
        internal uint Id;
        internal ushort Group;
        internal byte LogicalProcessorIndex;
        internal byte CoreIndex;
        internal byte LastLevelCacheIndex;
        internal byte NumaNodeIndex;
        internal byte EfficiencyClass;
        internal byte AllFlags;
        internal uint SchedulingClassAndReserved;
        internal ulong AllocationTag;
    }
}

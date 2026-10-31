using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Dismode.Core.Cpu;

namespace Dismode.Windows.Cpu;

/// <summary>
/// Laczy CPU Sets z rzeczywistymi rozmiarami i maskami L3 oraz pakietow.
/// </summary>
public static partial class SystemCpuTopologyProvider
{
    private const uint CpuSetInformationType = 0;
    private const uint RelationCache = 2;
    private const uint RelationProcessorPackage = 3;
    private const uint RelationAll = 0xffff;

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
                    0)
                || returned > required)
            {
                return null;
            }

            return ReadRelationships(new(ReadProcessors(buffer, returned)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static List<CpuLogicalProcessor> ReadProcessors(
        nint buffer,
        uint length)
    {
        List<CpuLogicalProcessor> processors = [];
        uint entrySize = checked((uint)Marshal.SizeOf<SystemCpuSetInformation>());
        uint offset = 0;

        // Cala struktura musi sie miescic w buforze, nie tylko jej naglowek:
        // PtrToStructure czyta pelne 32 bajty niezaleznie od tego, ile zostalo,
        // wiec sprawdzenie samego naglowka pozwalaloby czytac poza buforem.
        while (offset + entrySize <= length)
        {
            SystemCpuSetInformation entry =
                Marshal.PtrToStructure<SystemCpuSetInformation>(
                    buffer + checked((int)offset));
            if (entry.Size < entrySize || offset + entry.Size > length)
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
                    (entry.AllFlags & AllocatedFlag) != 0,
                    entry.LastLevelCacheIndex,
                    entry.NumaNodeIndex));
            }

            offset += entry.Size;
        }

        return processors;
    }

    private static CpuTopology ReadRelationships(CpuTopology topology)
    {
        uint required = 0;
        if (!GetLogicalProcessorInformationEx(RelationAll, nint.Zero, ref required)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            return topology;
        }

        if (required == 0 || required > 1024 * 1024)
        {
            return topology;
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            uint returned = required;
            if (!GetLogicalProcessorInformationEx(RelationAll, buffer, ref returned)
                || returned > required)
            {
                return topology;
            }

            byte[] information = new byte[checked((int)returned)];
            Marshal.Copy(buffer, information, 0, information.Length);
            return ReadRelationships(topology, information);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static CpuTopology ReadRelationships(CpuTopology topology, ReadOnlySpan<byte> information)
    {
        List<CpuCacheGroup> caches = [];
        List<CpuProcessorPackage> packages = [];
        while (!information.IsEmpty)
        {
            if (information.Length < 8)
            {
                return topology;
            }

            uint relationship = BinaryPrimitives.ReadUInt32LittleEndian(information);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(information[4..]);
            if (size < 8 || size > information.Length)
            {
                return topology;
            }

            ReadOnlySpan<byte> entry = information[..(int)size];
            if (relationship == RelationCache)
            {
                if (entry.Length < 40)
                {
                    return topology;
                }

                uint cacheType = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
                if (entry[8] == 3 && cacheType is 0 or 2)
                {
                    // Starszy CACHE_RELATIONSHIP ma jedno GroupMask, a pole
                    // GroupCount bylo zarezerwowane i moze pozostac zerowe.
                    ushort count = BinaryPrimitives.ReadUInt16LittleEndian(entry[38..]);
                    List<uint>? ids = ReadProcessorIds(entry[40..], Math.Max(1, (int)count), topology.Processors);
                    if (ids is null)
                    {
                        return topology;
                    }

                    caches.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]), ids));
                }
            }
            else if (relationship == RelationProcessorPackage)
            {
                if (entry.Length < 32)
                {
                    return topology;
                }

                ushort count = BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]);
                List<uint>? ids = ReadProcessorIds(entry[32..], count, topology.Processors);
                if (ids is null)
                {
                    return topology;
                }

                packages.Add(new(ids));
            }

            information = information[(int)size..];
        }

        return topology with { L3CacheGroups = caches, ProcessorPackages = packages };
    }

    private static List<uint>? ReadProcessorIds(
        ReadOnlySpan<byte> masks,
        int count,
        IReadOnlyList<CpuLogicalProcessor> processors)
    {
        int affinitySize = nint.Size == 8 ? 16 : 12;
        if (count == 0 || count > masks.Length / affinitySize)
        {
            return null;
        }

        List<uint> ids = [];
        for (int index = 0; index < count; index++)
        {
            ulong mask = nint.Size == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(masks)
                : BinaryPrimitives.ReadUInt32LittleEndian(masks);
            ushort group = BinaryPrimitives.ReadUInt16LittleEndian(masks[nint.Size..]);
            uint[] matching = [.. processors.Where(processor => processor.Group == group
                && processor.LogicalProcessorIndex < 64
                && (mask & (1UL << processor.LogicalProcessorIndex)) != 0)
                .Select(processor => processor.Id)];
            // Nie ufamy migawce, gdy maska opisuje nieznane CPU lub powtarza CPU.
            if (mask == 0 || matching.Length != BitOperations.PopCount(mask)
                || matching.Any(ids.Contains))
            {
                return null;
            }

            ids.AddRange(matching);
            masks = masks[affinitySize..];
        }

        return ids;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformationEx(
        uint relationship,
        nint information,
        ref uint returnedLength);

    /// <summary>
    /// SYSTEM_CPU_SET_INFORMATION. The union after the header is only ever the
    /// CpuSet variant today; Type is checked before the fields are trusted.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSetInformation
    {
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

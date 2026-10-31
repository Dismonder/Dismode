using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dismode.Core.Cpu;
using Dismode.Windows.Cpu;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class SystemCpuTopologyProviderTests
{
    private static CpuTopology SampleTopology() => new(
    [
        new(100, 0, 0, 0, 0, false, true, 0, 0),
        new(200, 0, 1, 1, 0, false, true, 1, 0),
    ]);

    [TestMethod]
    public void NativeL3AndPackageMasksMapToCpuSetIdsWithoutUsingCacheIndex()
    {
        byte[] information = [.. Cache(3, 96, (0, 2)), .. Cache(2, 1, (0, 3)),
            .. Cache(3, 32, (0, 1)), .. Package((0, 3))];

        CpuTopology topology = SystemCpuTopologyProvider.ReadRelationships(SampleTopology(), information);

        Assert.HasCount(2, topology.L3CacheGroups);
        Assert.AreEqual(96u * 1024 * 1024, topology.L3CacheGroups[0].SizeBytes);
        CollectionAssert.AreEqual(new uint[] { 200 }, topology.L3CacheGroups[0].ProcessorIds.ToArray());
        CollectionAssert.AreEqual(new uint[] { 100 }, topology.L3CacheGroups[1].ProcessorIds.ToArray());
        Assert.HasCount(1, topology.ProcessorPackages);
        CollectionAssert.AreEqual(new uint[] { 100, 200 }, topology.ProcessorPackages[0].ProcessorIds.ToArray());
    }

    [TestMethod]
    public void PackageAndCacheCanContainSeveralProcessorGroupMasks()
    {
        CpuTopology original = SampleTopology();
        original = original with
        {
            Processors = [original.Processors[0], original.Processors[1] with { Group = 1, LogicalProcessorIndex = 0 }],
        };
        byte[] information = [.. Cache(3, 96, (0, 1), (1, 1)), .. Package((0, 1), (1, 1))];

        CpuTopology topology = SystemCpuTopologyProvider.ReadRelationships(original, information);

        CollectionAssert.AreEqual(new uint[] { 100, 200 }, topology.L3CacheGroups[0].ProcessorIds.ToArray());
        CollectionAssert.AreEqual(new uint[] { 100, 200 }, topology.ProcessorPackages[0].ProcessorIds.ToArray());
    }

    [TestMethod]
    public void LegacyCacheRecordWithReservedGroupCountUsesOneMask()
    {
        byte[] cache = Cache(3, 96, (0, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(cache.AsSpan(38), 0);

        CpuTopology topology = SystemCpuTopologyProvider.ReadRelationships(
            SampleTopology(), [.. cache, .. Package((0, 3))]);

        Assert.HasCount(1, topology.L3CacheGroups);
        CollectionAssert.AreEqual(new uint[] { 100, 200 }, topology.L3CacheGroups[0].ProcessorIds.ToArray());
    }

    [TestMethod]
    [DataRow("truncated")]
    [DataRow("zero size")]
    [DataRow("short cache")]
    [DataRow("unknown processor")]
    [DataRow("unknown group")]
    [DataRow("too many masks")]
    [DataRow("duplicate mask")]
    [DataRow("empty mask")]
    public void InvalidNativeRecordsDiscardAllRelationshipData(string defect)
    {
        byte[] invalid = defect switch
        {
            "truncated" => Cache(3, 32, (0, 1))[..^1],
            "short cache" => Record(2, 16),
            "unknown processor" => Cache(3, 32, (0, 4)),
            "unknown group" => Package((1, 3)),
            "duplicate mask" => Package((0, 3), (0, 3)),
            "empty mask" => Cache(3, 32, (0, 0)),
            _ => Cache(3, 32, (0, 1)),
        };
        if (defect == "zero size")
        {
            BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(4), 0);
        }
        else if (defect == "too many masks")
        {
            BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(38), 2);
        }

        CpuTopology topology = SystemCpuTopologyProvider.ReadRelationships(
            SampleTopology(), [.. Cache(3, 96, (0, 2)), .. Package((0, 3)), .. invalid]);

        Assert.IsEmpty(topology.L3CacheGroups);
        Assert.IsEmpty(topology.ProcessorPackages);
        Assert.HasCount(2, topology.Processors);
    }

    [TestMethod]
    public void CpuSetsExposeNumaNodeIndex()
    {
        byte[] record = Record(0, 32);
        // SYSTEM_CPU_SET_INFORMATION zaczyna sie od Size, potem Type.
        BinaryPrimitives.WriteUInt32LittleEndian(record, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 100);
        record[17] = 2;
        nint buffer = Marshal.AllocHGlobal(record.Length);
        try
        {
            Marshal.Copy(record, 0, buffer, record.Length);
            List<CpuLogicalProcessor> processors = SystemCpuTopologyProvider.ReadProcessors(buffer, 32);

            Assert.HasCount(1, processors);
            Assert.AreEqual((byte?)2, processors[0].NumaNodeIndex);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] Cache(byte level, uint sizeMiB, params (ushort Group, ulong Mask)[] masks)
    {
        byte[] record = Record(2, 40 + masks.Length * AffinitySize);
        record[8] = level;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), sizeMiB * 1024 * 1024);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(38), (ushort)masks.Length);
        WriteMasks(record.AsSpan(40), masks);
        return record;
    }

    private static byte[] Package(params (ushort Group, ulong Mask)[] masks)
    {
        byte[] record = Record(3, 32 + masks.Length * AffinitySize);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)masks.Length);
        WriteMasks(record.AsSpan(32), masks);
        return record;
    }

    private static byte[] Record(uint relationship, int size)
    {
        byte[] record = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(record, relationship);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)size);
        return record;
    }

    private static int AffinitySize => nint.Size == 8 ? 16 : 12;

    private static void WriteMasks(Span<byte> destination, (ushort Group, ulong Mask)[] masks)
    {
        foreach ((ushort group, ulong mask) in masks)
        {
            if (nint.Size == 8)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(destination, mask);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination, checked((uint)mask));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination[nint.Size..], group);
            destination = destination[AffinitySize..];
        }
    }
}

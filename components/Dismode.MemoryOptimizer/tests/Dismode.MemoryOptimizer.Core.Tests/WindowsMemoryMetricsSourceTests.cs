using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryOptimizer.Core.Native;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class WindowsMemoryMetricsSourceTests
{
    [TestMethod]
    public void MapperUsesPageFileFieldsForCommitInsteadOfVirtualAddressSpace()
    {
        MemoryStatusEx status = new()
        {
            MemoryLoad = 75,
            TotalPhysical = 16_000,
            AvailablePhysical = 4_000,
            TotalPageFile = 24_000,
            AvailablePageFile = 6_000,
            TotalVirtual = 128_000_000,
            AvailableVirtual = 127_000_000,
        };

        MemorySnapshot snapshot = WindowsMemoryMetricsMapper.Map(
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
            status);

        Assert.AreEqual((ulong)24_000, snapshot.CommitLimitBytes);
        Assert.AreEqual((ulong)6_000, snapshot.AvailableCommitBytes);
        Assert.AreEqual((ulong)18_000, snapshot.CommittedBytes);
        Assert.AreEqual((ulong)128_000_000, snapshot.TotalVirtualBytes);
        Assert.AreEqual((ulong)127_000_000, snapshot.AvailableVirtualBytes);
    }

    [TestMethod]
    public void MapperHandlesZeroCommitValues()
    {
        MemorySnapshot snapshot = MapCommitValues(0, 0);

        Assert.AreEqual((ulong)0, snapshot.CommitLimitBytes);
        Assert.AreEqual((ulong)0, snapshot.AvailableCommitBytes);
        Assert.AreEqual((ulong)0, snapshot.CommittedBytes);
    }

    [TestMethod]
    public void MapperSaturatesCommittedBytesWhenAvailableCommitExceedsLimit()
    {
        MemorySnapshot snapshot = MapCommitValues(100, 101);

        Assert.AreEqual((ulong)0, snapshot.CommittedBytes);
    }

    [TestMethod]
    public void MapperPreservesMaximumCommitLimit()
    {
        MemorySnapshot snapshot = MapCommitValues(ulong.MaxValue, 0);

        Assert.AreEqual(ulong.MaxValue, snapshot.CommitLimitBytes);
        Assert.AreEqual(ulong.MaxValue, snapshot.CommittedBytes);
    }

    private static MemorySnapshot MapCommitValues(
        ulong totalPageFile,
        ulong availablePageFile)
    {
        MemoryStatusEx status = new()
        {
            TotalPhysical = 1_000,
            AvailablePhysical = 500,
            TotalPageFile = totalPageFile,
            AvailablePageFile = availablePageFile,
            TotalVirtual = 2_000,
            AvailableVirtual = 1_000,
        };

        return WindowsMemoryMetricsMapper.Map(DateTimeOffset.UnixEpoch, status);
    }
}

using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class SystemMemoryCleanerTests
{
    [TestMethod]
    public void PurgeMemoryReturnsValidResultStructure()
    {
        SystemMemoryPurgeResult result = SystemMemoryCleaner.PurgeMemory();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.TotalWorkingSetFreedBytes >= 0);
        Assert.IsTrue(result.ProcessesTrimmedCount >= 0);
    }

    [TestMethod]
    public void PurgeMemoryWithCurrentProcessSkipsSelfGracefully()
    {
        SystemMemoryPurgeResult result = SystemMemoryCleaner.PurgeMemory([Environment.ProcessId]);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.ProcessesTrimmedCount);
    }
}

using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class SystemMemoryCleanerTests
{
    [TestInitialize]
    public void RequireExplicitVmFlag()
    {
        if (!StringComparer.Ordinal.Equals(
                Environment.GetEnvironmentVariable(
                    "GAMESHIFT_ALLOW_AGGRESSIVE_MEMORY_TESTS"),
                "1"))
        {
            Assert.Inconclusive(
                "System file cache mutations are allowed only on a disposable " +
                "Windows 11 VM with GAMESHIFT_ALLOW_AGGRESSIVE_MEMORY_TESTS=1.");
        }
    }

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

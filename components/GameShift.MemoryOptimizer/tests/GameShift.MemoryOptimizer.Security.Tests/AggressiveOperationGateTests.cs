namespace GameShift.MemoryOptimizer.Security.Tests;

[TestClass]
public sealed class AggressiveOperationGateTests
{
    [TestMethod]
    public void AggressiveNativeTestsRequireExplicitVmFlag()
    {
        string? value = Environment.GetEnvironmentVariable(
            "GAMESHIFT_ALLOW_AGGRESSIVE_MEMORY_TESTS");

        if (!StringComparer.Ordinal.Equals(value, "1"))
        {
            Assert.Inconclusive(
                "Aggressive native operations are allowed only on a disposable " +
                "Windows 11 VM with GAMESHIFT_ALLOW_AGGRESSIVE_MEMORY_TESTS=1.");
        }

        Assert.AreEqual("1", value);
    }
}

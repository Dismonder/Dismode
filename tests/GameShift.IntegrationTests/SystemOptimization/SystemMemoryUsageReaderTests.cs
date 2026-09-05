using GameShift.Windows.SystemOptimization;

namespace GameShift.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemMemoryUsageReaderTests
{
    [TestMethod]
    public void MapsUsedPhysicalMemoryWithoutUnderflowOrOverflow()
    {
        Assert.AreEqual(
            12_000L,
            SystemMemoryUsageReader.MapUsedPhysicalMemoryBytes(
                totalPhysicalBytes: 16_000,
                availablePhysicalBytes: 4_000));
        Assert.IsNull(
            SystemMemoryUsageReader.MapUsedPhysicalMemoryBytes(
                totalPhysicalBytes: 4_000,
                availablePhysicalBytes: 16_000));
        Assert.IsNull(
            SystemMemoryUsageReader.MapUsedPhysicalMemoryBytes(
                totalPhysicalBytes: ulong.MaxValue,
                availablePhysicalBytes: 0));
    }
}

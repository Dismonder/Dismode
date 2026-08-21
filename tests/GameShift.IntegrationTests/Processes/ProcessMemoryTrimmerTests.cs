using System.Diagnostics;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class ProcessMemoryTrimmerTests
{
    [TestMethod]
    public async Task TryTrimWorkingSetSucceedsOnRunningProcess()
    {
        await using ProcessHarnessFixture fixture =
            await ProcessHarnessFixture.StartAsync();

        bool trimmed = ProcessMemoryTrimmer.TryTrimWorkingSet(
            fixture.Process,
            out long freedBytes);

        Assert.IsTrue(trimmed);
        Assert.IsTrue(freedBytes >= 0);
    }

    [TestMethod]
    public void TryTrimWorkingSetReturnsFalseOnInvalidProcessId()
    {
        bool trimmed = ProcessMemoryTrimmer.TryTrimWorkingSet(
            -99999,
            out long freedBytes);

        Assert.IsFalse(trimmed);
        Assert.AreEqual(0, freedBytes);
    }
}

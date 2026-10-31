using Dismode.Core.Cpu;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class CpuSetsSteeringTests
{
    [TestMethod]
    public void GameCpuSetsSelectTheProcessorsFromTheDesiredMask()
    {
        // Gra: maska 0b0101 daje dwa procesory. Proces testowy ma pelna
        // maske, wiec zbiory nie sa przykryte affinity.
        CpuTopology topology = new(
        [
            new(100, 0, 0, 0, 1, false, true),
            new(101, 0, 1, 1, 0, false, true),
            new(102, 0, 2, 2, 1, false, true),
            new(103, 0, 3, 3, 0, false, true),
        ]);
        int self = Environment.ProcessId;

        uint[]? selected = LocalGameSessionOrchestrator.TryGetCpuSetIds(
            self,
            topology,
            0b0101);
        CollectionAssert.AreEquivalent(new uint[] { 100, 102 }, selected);
    }
}

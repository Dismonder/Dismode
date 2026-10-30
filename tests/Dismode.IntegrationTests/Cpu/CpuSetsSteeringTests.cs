using Dismode.Core.Cpu;
using Dismode.Windows.Cpu;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class CpuSetsSteeringTests
{
    private const int ProcessId = 4242;

    [TestMethod]
    public void EmptyOriginalIsWrittenConfirmedAndRestoredToEmpty()
    {
        FakeCpuSetsPort port = new();

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.Applied, steer.Outcome);
        Assert.IsEmpty(steer.Original);
        CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, steer.Applied.ToArray());
        CollectionAssert.AreEquivalent(new uint[] { 1, 2 }, port.Current);

        CpuSetsRestoreResult restored = CpuSetsSteering.RestoreIfUnchanged(
            port,
            ProcessId,
            steer.Original,
            steer.Applied);

        Assert.AreEqual(CpuSetsRestoreResult.Restored, restored);
        Assert.IsEmpty(port.Current);
        Assert.AreEqual(2, port.Writes);
    }

    [TestMethod]
    public void NonEmptyForeignAssignmentIsNotOverwritten()
    {
        FakeCpuSetsPort port = new() { Current = [7, 8] };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.SkippedForeign, steer.Outcome);
        Assert.IsFalse(steer.IsApplied);
        Assert.AreEqual(0, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 7, 8 }, port.Current);
    }

    [TestMethod]
    public void TargetEqualToOriginalIsANoOpEvenInADifferentOrder()
    {
        FakeCpuSetsPort port = new() { Current = [2, 1] };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.NoChange, steer.Outcome);
        Assert.IsFalse(steer.IsApplied, "Nic nie zapisano, wiec nie ma czego przywracac.");
        Assert.AreEqual(0, port.Writes);
    }

    [TestMethod]
    public void QueryFailureSkipsWithoutWriting()
    {
        FakeCpuSetsPort port = new() { QueryFails = true };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.SkippedQueryFailed, steer.Outcome);
        Assert.AreEqual(0, port.Writes);
    }

    [TestMethod]
    public void RefusedWriteIsNotApplied()
    {
        FakeCpuSetsPort port = new() { ApplyFails = true };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.NotApplied, steer.Outcome);
        Assert.AreEqual(1, port.Writes);
    }

    [TestMethod]
    public void MismatchedReadBackIsNotAppliedAndNothingIsUndone()
    {
        // System przyjal zapis, ale odczyt pokazuje cos innego. To nie jest
        // juz dowodnie nasze przypisanie, wiec ani zastosowania, ani cofania.
        FakeCpuSetsPort port = new() { ReadBackAfterApply = [9] };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.NotApplied, steer.Outcome);
        Assert.AreEqual(1, port.Writes, "Zadnego cofania po niezgodnym odczycie.");
        CollectionAssert.AreEqual(new uint[] { 9 }, port.Current);
    }

    [TestMethod]
    public void UnreadableReadBackClearsBackToTheEmptyOriginal()
    {
        // Zapis przeszedl, odczytu zwrotnego nie ma: nasz zapis moze lezec
        // na procesie, a oryginal byl pusty.
        FakeCpuSetsPort port = new() { QueryFailsAfterApply = true };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.NotApplied, steer.Outcome);
        Assert.AreEqual(2, port.Writes);
        Assert.IsEmpty(port.Written[1]);
        Assert.IsEmpty(port.Current);
    }

    [TestMethod]
    public void ForeignReadBackAfterOurWriteIsNeverCleared()
    {
        FakeCpuSetsPort port = new() { ReadBackAfterApply = [9] };

        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);

        Assert.AreEqual(CpuSetsSteerOutcome.NotApplied, steer.Outcome);
        Assert.AreEqual(1, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 9 }, port.Current);
    }

    [TestMethod]
    public void ExternalChangeBeforeRestoreIsLeftAlone()
    {
        FakeCpuSetsPort port = new();
        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);
        port.Current = [3];

        CpuSetsRestoreResult restored = CpuSetsSteering.RestoreIfUnchanged(
            port,
            ProcessId,
            steer.Original,
            steer.Applied);

        Assert.AreEqual(CpuSetsRestoreResult.LeftAlone, restored);
        Assert.AreEqual(1, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 3 }, port.Current);
    }

    [TestMethod]
    public void AssignmentClearedByOthersBeforeRestoreIsLeftAlone()
    {
        FakeCpuSetsPort port = new();
        CpuSetsSteer steer = CpuSetsSteering.Steer(port, ProcessId, [1, 2]);
        port.Current = [];

        Assert.AreEqual(
            CpuSetsRestoreResult.LeftAlone,
            CpuSetsSteering.RestoreIfUnchanged(
                port,
                ProcessId,
                steer.Original,
                steer.Applied));
        Assert.AreEqual(1, port.Writes);
    }

    [TestMethod]
    public void RestoreComparesSetsNotOrderAndPutsTheOriginalBack()
    {
        FakeCpuSetsPort port = new() { Current = [2, 1] };

        CpuSetsRestoreResult restored = CpuSetsSteering.RestoreIfUnchanged(
            port,
            ProcessId,
            original: [5],
            applied: [1, 2]);

        Assert.AreEqual(CpuSetsRestoreResult.Restored, restored);
        CollectionAssert.AreEqual(new uint[] { 5 }, port.Current);
    }

    [TestMethod]
    public void RestoreFailsWhenTheAssignmentCannotBeRead()
    {
        FakeCpuSetsPort port = new() { QueryFails = true };

        Assert.AreEqual(
            CpuSetsRestoreResult.Failed,
            CpuSetsSteering.RestoreIfUnchanged(port, ProcessId, [], [1]));
        Assert.AreEqual(0, port.Writes);
    }

    [TestMethod]
    public void GameCpuSetsFollowTheSameRules()
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

        FakeCpuSetsPort empty = new();
        CpuSetsSteer applied = LocalGameSessionOrchestrator.TryApplyCpuSets(
            empty,
            self,
            topology,
            0b0101);
        Assert.AreEqual(CpuSetsSteerOutcome.Applied, applied.Outcome);
        CollectionAssert.AreEquivalent(new uint[] { 100, 102 }, empty.Current);

        FakeCpuSetsPort foreign = new() { Current = [103] };
        Assert.AreEqual(
            CpuSetsSteerOutcome.SkippedForeign,
            LocalGameSessionOrchestrator.TryApplyCpuSets(
                foreign,
                self,
                topology,
                0b0101).Outcome);
        Assert.AreEqual(0, foreign.Writes);

        FakeCpuSetsPort same = new() { Current = [102, 100] };
        Assert.AreEqual(
            CpuSetsSteerOutcome.NoChange,
            LocalGameSessionOrchestrator.TryApplyCpuSets(
                same,
                self,
                topology,
                0b0101).Outcome);
        Assert.AreEqual(0, same.Writes);

        FakeCpuSetsPort unreadable = new() { QueryFails = true };
        Assert.AreEqual(
            CpuSetsSteerOutcome.SkippedQueryFailed,
            LocalGameSessionOrchestrator.TryApplyCpuSets(
                unreadable,
                self,
                topology,
                0b0101).Outcome);
        Assert.AreEqual(0, unreadable.Writes);
    }
}

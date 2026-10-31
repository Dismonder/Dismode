using Dismode.Core.Recovery;
using Dismode.SystemAgent.SystemOptimization;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class WindowsSystemTweakRuntimeTests
{
    [TestMethod]
    public void UnchangedActionRecoveryWithoutJournalIsSuccessWhenAccepted()
    {
        Assert.IsTrue(WindowsSystemTweakRuntime.IsRecoverySuccess(
            ActionRecoveryStatus.MissingPreparation,
            acceptMissingPreparation: true));
    }

    [TestMethod]
    public void MissingPreparationStaysFailureForActionsThatAlwaysJournal()
    {
        Assert.IsFalse(WindowsSystemTweakRuntime.IsRecoverySuccess(
            ActionRecoveryStatus.MissingPreparation,
            acceptMissingPreparation: false));
    }

    [TestMethod]
    [DataRow(ActionRecoveryStatus.Restored)]
    [DataRow(ActionRecoveryStatus.AlreadyRestored)]
    [DataRow(ActionRecoveryStatus.NotRequired)]
    public void RestoredStatusesAreAlwaysSuccess(ActionRecoveryStatus status)
    {
        Assert.IsTrue(WindowsSystemTweakRuntime.IsRecoverySuccess(status, false));
        Assert.IsTrue(WindowsSystemTweakRuntime.IsRecoverySuccess(status, true));
    }

    [TestMethod]
    public void ConflictIsNeverSuccess()
    {
        Assert.IsFalse(WindowsSystemTweakRuntime.IsRecoverySuccess(
            ActionRecoveryStatus.ConflictRequiresDecision,
            acceptMissingPreparation: true));
    }
}

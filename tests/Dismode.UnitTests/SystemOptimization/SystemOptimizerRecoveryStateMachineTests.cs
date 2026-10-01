using Dismode.Contracts.SystemOptimization;
using Dismode.Core.SystemOptimization;

namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerRecoveryStateMachineTests
{
    [TestMethod]
    public void RebootExperimentUsesOnlyDocumentedForwardTransitions()
    {
        RecoveryPhase phase = RecoveryPhase.Baseline;
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.SnapshotCaptured);
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.CandidatePendingReboot);
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.PostBootVerification);
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.Capture);
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.RestorePendingReboot);
        phase = SystemOptimizerRecoveryStateMachine.Transition(
            phase,
            RecoveryPhase.VerifiedRestored);

        Assert.AreEqual(RecoveryPhase.VerifiedRestored, phase);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            SystemOptimizerRecoveryStateMachine.Transition(
                RecoveryPhase.VerifiedRestored,
                RecoveryPhase.CandidatePendingReboot));
    }

    [TestMethod]
    public void MissingConfirmationAfterThirtyMinutesRequiresRestore()
    {
        DateTimeOffset enteredUtc = new(
            2026,
            8,
            29,
            10,
            0,
            0,
            TimeSpan.Zero);

        RecoveryTimeoutDecision before =
            SystemOptimizerRecoveryStateMachine.EvaluateTimeout(
                RecoveryPhase.CandidatePendingReboot,
                enteredUtc,
                enteredUtc.AddMinutes(29));
        RecoveryTimeoutDecision after =
            SystemOptimizerRecoveryStateMachine.EvaluateTimeout(
                RecoveryPhase.CandidatePendingReboot,
                enteredUtc,
                enteredUtc.AddMinutes(30));

        Assert.AreEqual(RecoveryTimeoutDecision.Wait, before);
        Assert.AreEqual(RecoveryTimeoutDecision.Restore, after);
    }
}

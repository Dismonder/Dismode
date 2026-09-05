using GameShift.Contracts.SystemOptimization;

namespace GameShift.Core.SystemOptimization;

public static class SystemOptimizerRecoveryStateMachine
{
    public static readonly TimeSpan ConfirmationTimeout =
        TimeSpan.FromMinutes(30);

    public static RecoveryPhase Transition(
        RecoveryPhase current,
        RecoveryPhase next)
    {
        if (!IsAllowed(current, next))
        {
            throw new InvalidOperationException(
                $"Recovery transition from {current} to {next} is not allowed.");
        }

        return next;
    }

    public static RecoveryTimeoutDecision EvaluateTimeout(
        RecoveryPhase phase,
        DateTimeOffset phaseEnteredAtUtc,
        DateTimeOffset nowUtc)
    {
        if (phase is not (
                RecoveryPhase.CandidatePendingReboot
                or RecoveryPhase.PostBootVerification
                or RecoveryPhase.Capture))
        {
            return RecoveryTimeoutDecision.None;
        }

        return nowUtc.ToUniversalTime() - phaseEnteredAtUtc.ToUniversalTime()
                >= ConfirmationTimeout
            ? RecoveryTimeoutDecision.Restore
            : RecoveryTimeoutDecision.Wait;
    }

    private static bool IsAllowed(RecoveryPhase current, RecoveryPhase next) =>
        (current, next) switch
        {
            (RecoveryPhase.Baseline, RecoveryPhase.SnapshotCaptured) => true,
            (RecoveryPhase.SnapshotCaptured, RecoveryPhase.CandidatePendingReboot) => true,
            (RecoveryPhase.SnapshotCaptured, RecoveryPhase.Capture) => true,
            (RecoveryPhase.CandidatePendingReboot, RecoveryPhase.PostBootVerification) => true,
            (RecoveryPhase.PostBootVerification, RecoveryPhase.Capture) => true,
            (RecoveryPhase.Capture, RecoveryPhase.RestorePendingReboot) => true,
            (RecoveryPhase.Capture, RecoveryPhase.PromotedGlobally) => true,
            (RecoveryPhase.RestorePendingReboot, RecoveryPhase.VerifiedRestored) => true,
            (_, RecoveryPhase.Failed) when current != RecoveryPhase.VerifiedRestored => true,
            _ => false,
        };
}

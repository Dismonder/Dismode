namespace GameShift.Core.Recovery;

public sealed record SessionRecoveryResult(
    IReadOnlyList<SessionActionRecovery> Actions)
{
    public bool RequiresManualIntervention =>
        Actions.Any(action =>
            action.Result.Status is
                ActionRecoveryStatus.ConflictRequiresDecision
                or ActionRecoveryStatus.VerificationFailed
                or ActionRecoveryStatus.MissingPreparation);
}


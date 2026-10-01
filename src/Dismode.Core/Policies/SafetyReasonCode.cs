namespace Dismode.Core.Policies;

public enum SafetyReasonCode
{
    ReadOnlyAction = 1,
    VerifiedRecovery = 2,
    ProtectedTarget = 3,
    RecoveryNotVerified = 4,
    TargetNotApproved = 5,
    TargetNotOwnedByDismode = 6,
    DesktopPowerPlanPreserved = 7,
}


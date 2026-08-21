namespace GameShift.Core.Recovery;

public enum ActionRecoveryStatus
{
    NotRequired = 1,
    Restored = 2,
    AlreadyRestored = 3,
    ConflictRequiresDecision = 4,
    VerificationFailed = 5,
    MissingPreparation = 6,
}


namespace GameShift.Core.Transactions;

public enum ActionExecutionStatus
{
    AppliedAndVerified = 1,
    Blocked = 2,
    AlreadyCompleted = 3,
    RecoveryRequired = 4,
    VerificationFailed = 5,
}


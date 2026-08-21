namespace GameShift.Core.Journal;

public enum JournalEventKind
{
    ActionPrepared = 1,
    ActionBlocked = 2,
    ActionApplying = 3,
    ActionApplied = 4,
    ActionVerified = 5,
    ActionVerificationFailed = 6,
    ActionApplyFailed = 7,
    RestoreStarted = 8,
    ActionCompensating = 9,
    ActionCompensated = 10,
    CompensationVerified = 11,
    CompensationVerificationFailed = 12,
    ExternalConflictDetected = 13,
    SessionCheckpointRecorded = 100,
}

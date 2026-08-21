namespace GameShift.Core.Journal;

public enum SessionCheckpoint
{
    SnapshotComplete = 1,
    ServicesApplied = 2,
    ProcessesApplied = 3,
    PowerApplied = 4,
    SessionActivated = 5,
    RestoreStarted = 6,
    ServicesRestored = 7,
    ProcessesRestored = 8,
    PowerRestored = 9,
    ReconciliationComplete = 10,
    GameLaunched = 11,
    GameCloseRequested = 12,
    GameProcessTreeObserved = 13,
    GameForceTerminated = 14,
}

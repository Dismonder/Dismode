namespace Dismode.Core.Journal;

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

    /// <summary>
    /// The set of processes the reactive restraint loop currently holds
    /// changed. Recorded so a host crash leaves enough in the journal to
    /// reverse restraints nobody planned in advance.
    /// </summary>
    BackgroundRestraintChanged = 15,
}

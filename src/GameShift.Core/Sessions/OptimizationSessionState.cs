namespace GameShift.Core.Sessions;

public enum OptimizationSessionState
{
    Idle = 0,
    Discovering = 1,
    Profiling = 2,
    Planning = 3,
    AwaitingApproval = 4,
    Preflight = 5,
    Snapshotting = 6,
    Applying = 7,
    Verifying = 8,
    LaunchingGame = 9,
    Active = 10,
    Degraded = 11,
    GameCrashed = 12,
    UserRestoreRequested = 13,
    GameExited = 14,
    Restoring = 15,
    Reconciling = 16,
    Completed = 17,
    CompletedWithWarnings = 18,
    RecoveryRequired = 19,
}


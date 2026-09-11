namespace GameShift.Core.Actions;

public enum OptimizationActionKind
{
    Observe = 0,
    Ignore = 1,
    ReduceProcessPriority = 2,
    ApplyProcessEcoQos = 3,
    GracefulCloseApplication = 4,
    StopApprovedService = 5,
    ActivateManagedPowerProfile = 6,
    DeferApprovedScheduledTask = 7,
    SuppressApprovedRelaunch = 8,
    AssignOwnedProcessJob = 9,
    BoostGamePriority = 10,
    ConfigureHibernation = 11,
    ConfigureSystemTweak = 12,
    RestrictProcessAffinity = 13,
    LowerProcessMemoryPriority = 14,
    LowerProcessIoPriority = 15,
}

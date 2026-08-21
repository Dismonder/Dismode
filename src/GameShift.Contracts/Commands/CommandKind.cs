namespace GameShift.Contracts.Commands;

public enum CommandKind
{
    GetComponentStatus = 1,
    DiscoverSystem = 2,
    StartOptimizationSession = 3,
    ApproveOptimizationPlan = 4,
    RestoreOptimizationSession = 5,
    StopApprovedService = 6,
    SetProcessPriority = 7,
    SetProcessEcoQos = 8,
    ActivateManagedPowerProfile = 9,
    GetActiveSession = 10,
    CloseGame = 11,
}

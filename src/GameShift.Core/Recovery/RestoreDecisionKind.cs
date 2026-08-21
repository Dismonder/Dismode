namespace GameShift.Core.Recovery;

public enum RestoreDecisionKind
{
    NoActionAlreadyOriginal = 1,
    RestoreOriginal = 2,
    PreserveCurrentAndReportConflict = 3,
}


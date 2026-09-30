namespace Dismode.Core.History;

public enum SessionCompletionStatus
{
    Completed = 1,
    RestoredWithConflicts = 2,
    PartiallyRestored = 3,
    RecoveredAfterCrash = 4,
    FailedBeforeApply = 5,
}

namespace Dismode.Core.Recovery;

public sealed record RestoreDecision<TState>(
    RestoreDecisionKind Kind,
    TState OriginalState,
    TState AppliedState,
    TState CurrentState)
    where TState : notnull
{
    public bool ShouldCompensate => Kind == RestoreDecisionKind.RestoreOriginal;

    public bool HasConflict => Kind == RestoreDecisionKind.PreserveCurrentAndReportConflict;
}


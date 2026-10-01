namespace Dismode.Core.Recovery;

public static class ThreeWayReconciler
{
    public static RestoreDecision<TState> Decide<TState>(
        TState originalState,
        TState appliedState,
        TState currentState,
        IEqualityComparer<TState>? comparer = null)
        where TState : notnull
    {
        comparer ??= EqualityComparer<TState>.Default;

        if (comparer.Equals(currentState, originalState))
        {
            return new(
                RestoreDecisionKind.NoActionAlreadyOriginal,
                originalState,
                appliedState,
                currentState);
        }

        if (comparer.Equals(currentState, appliedState))
        {
            return new(
                RestoreDecisionKind.RestoreOriginal,
                originalState,
                appliedState,
                currentState);
        }

        return new(
            RestoreDecisionKind.PreserveCurrentAndReportConflict,
            originalState,
            appliedState,
            currentState);
    }
}


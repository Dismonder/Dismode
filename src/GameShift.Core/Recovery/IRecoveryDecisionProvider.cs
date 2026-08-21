namespace GameShift.Core.Recovery;

public interface IRecoveryDecisionProvider<TState>
    where TState : notnull
{
    RestoreDecision<TState> DecideRecovery(
        TState originalState,
        TState appliedState,
        TState currentState);
}

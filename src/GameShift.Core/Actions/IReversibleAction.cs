namespace GameShift.Core.Actions;

public interface IReversibleAction<TState>
    where TState : notnull
{
    ActionDescriptor Descriptor { get; }

    ValueTask<PreparedAction<TState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken);

    ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<TState> preparedAction,
        CancellationToken cancellationToken);

    ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<TState> preparedAction,
        CancellationToken cancellationToken);

    ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<TState> preparedAction,
        CancellationToken cancellationToken);

    ValueTask<TState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken);

    ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<TState> preparedAction,
        CancellationToken cancellationToken);

    ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<TState> preparedAction,
        CancellationToken cancellationToken);
}

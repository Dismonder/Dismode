using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed class ProcessPriorityAction :
    IReversibleAction<ProcessPriorityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly ProcessPriorityClass _desiredPriority;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessPriorityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        ProcessPriorityClass desiredPriority,
        IProcessIdentityProvider? identityProvider = null)
    {
        if (desiredPriority is not (
            ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredPriority),
                desiredPriority,
                "GameShift may only lower a process to BelowNormal or Idle.");
        }

        _expectedIdentity = expectedIdentity;
        _desiredPriority = desiredPriority;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.ReduceProcessPriority);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessPriorityState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        ProcessPriorityState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessPriorityState(_desiredPriority),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessPriorityState currentState =
                await ReadCurrentStateAsync(context, cancellationToken)
                    .ConfigureAwait(false);

            if (currentState != preparedAction.OriginalState)
            {
                return ActionValidationResult.Blocked(
                    "The process priority changed after preparation.");
            }

            return IsSafeReduction(
                preparedAction.OriginalState.PriorityClass,
                preparedAction.DesiredState.PriorityClass)
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "Safe mode only allows Normal to BelowNormal or "
                    + "BelowNormal to Idle.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionValidationResult.Blocked(exception.Message);
        }
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        process.PriorityClass = preparedAction.DesiredState.PriorityClass;
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessPriorityState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        return new(process.PriorityClass);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        process.PriorityClass = preparedAction.OriginalState.PriorityClass;
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    private static bool IsSafeReduction(
        ProcessPriorityClass original,
        ProcessPriorityClass desired) =>
        original == desired
        || (original == ProcessPriorityClass.Normal
            && desired == ProcessPriorityClass.BelowNormal)
        || (original == ProcessPriorityClass.BelowNormal
            && desired == ProcessPriorityClass.Idle);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessPriorityState expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            return process.PriorityClass == expectedState.PriorityClass
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    "The observed process priority does not match the expected state.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}

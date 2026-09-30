using System.ComponentModel;
using System.Diagnostics;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Recovery;

namespace Dismode.Windows.Processes;

public sealed class RuntimeProcessPriorityAction :
    IReversibleAction<RuntimeProcessPriorityState>,
    IRecoveryDecisionProvider<RuntimeProcessPriorityState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly ProcessPriorityClass _desiredPriority;
    private readonly IProcessIdentityProvider _identityProvider;

    public RuntimeProcessPriorityAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        ProcessPriorityClass desiredPriority,
        IProcessIdentityProvider? identityProvider = null)
    {
        if (desiredPriority is not (
            ProcessPriorityClass.BelowNormal
            or ProcessPriorityClass.AboveNormal
            or ProcessPriorityClass.High))
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredPriority),
                desiredPriority,
                "Dismode permits BelowNormal for optional background "
                + "processes and AboveNormal or High for an approved game; "
                + "Realtime is always blocked.");
        }

        _expectedIdentity = expectedIdentity;
        _desiredPriority = desiredPriority;
        _identityProvider =
            identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            desiredPriority == ProcessPriorityClass.BelowNormal
                ? OptimizationActionKind.ReduceProcessPriority
                : OptimizationActionKind.BoostGamePriority);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<RuntimeProcessPriorityState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        RuntimeProcessPriorityState original =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        if (!original.IsRunning || original.PriorityClass is null)
        {
            throw new InvalidOperationException(
                "The approved game process is no longer running.");
        }

        return new(
            original,
            new(IsRunning: true, _desiredPriority),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeProcessPriorityState current =
                await ReadCurrentStateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            if (current != preparedAction.OriginalState)
            {
                return ActionValidationResult.Blocked(
                    "The game priority changed after preparation.");
            }

            if (_desiredPriority == ProcessPriorityClass.BelowNormal)
            {
                return current.PriorityClass is
                    ProcessPriorityClass.Normal
                    or ProcessPriorityClass.BelowNormal
                        ? ActionValidationResult.Allowed()
                        : ActionValidationResult.Blocked(
                            "Dismode lowers only Normal or already "
                            + "BelowNormal optional processes.");
            }

            return current.PriorityClass is
                    ProcessPriorityClass.Idle
                    or ProcessPriorityClass.BelowNormal
                    or ProcessPriorityClass.Normal
                    or ProcessPriorityClass.AboveNormal
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "Dismode does not override an existing High or "
                    + "Realtime game priority.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionValidationResult.Blocked(exception.Message);
        }
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        process.PriorityClass = _desiredPriority;
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessPriorityState> preparedAction,
        CancellationToken cancellationToken) =>
        VerifyStateAsync(
            preparedAction.DesiredState,
            allowMissingProcess: false,
            cancellationToken);

    public async ValueTask<RuntimeProcessPriorityState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken) =>
        await ReadCurrentStateCoreAsync(cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<RuntimeProcessPriorityState>
        ReadCurrentStateCoreAsync(
            CancellationToken cancellationToken)
    {
        bool matches = await _identityProvider
            .MatchesRuntimeIdentityAsync(
                _expectedIdentity,
                cancellationToken)
            .ConfigureAwait(false);
        if (!matches)
        {
            return new(IsRunning: false, PriorityClass: null);
        }

        using Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        return new(IsRunning: true, process.PriorityClass);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessPriorityState> preparedAction,
        CancellationToken cancellationToken)
    {
        RuntimeProcessPriorityState current =
            await ReadCurrentStateCoreAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!current.IsRunning)
        {
            return;
        }

        using Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        process.PriorityClass =
            preparedAction.OriginalState.PriorityClass
            ?? throw new InvalidDataException(
                "The original game priority is missing.");
    }

    public ValueTask<ActionVerificationResult>
        VerifyCompensationAsync(
            ActionExecutionContext context,
            PreparedAction<RuntimeProcessPriorityState> preparedAction,
            CancellationToken cancellationToken) =>
        VerifyStateAsync(
            preparedAction.OriginalState,
            allowMissingProcess: true,
            cancellationToken);

    public RestoreDecision<RuntimeProcessPriorityState> DecideRecovery(
        RuntimeProcessPriorityState originalState,
        RuntimeProcessPriorityState appliedState,
        RuntimeProcessPriorityState currentState) =>
        !currentState.IsRunning
            ? new(
                RestoreDecisionKind.NoActionAlreadyOriginal,
                originalState,
                appliedState,
                currentState)
            : ThreeWayReconciler.Decide(
                originalState,
                appliedState,
                currentState);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        RuntimeProcessPriorityState expected,
        bool allowMissingProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeProcessPriorityState current =
                await ReadCurrentStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (allowMissingProcess && !current.IsRunning)
            {
                return ActionVerificationResult.Verified();
            }

            return current == expected
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    "The game process priority does not match the expected state.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Recovery;

namespace GameShift.Windows.Processes;

public sealed class RuntimeProcessEcoQosAction :
    IReversibleAction<RuntimeProcessEcoQosState>,
    IRecoveryDecisionProvider<RuntimeProcessEcoQosState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly bool _desiredEnabled;
    private readonly IProcessIdentityProvider _identityProvider;

    public RuntimeProcessEcoQosAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        bool desiredEnabled = true,
        IProcessIdentityProvider? identityProvider = null)
    {
        _expectedIdentity = expectedIdentity;
        _desiredEnabled = desiredEnabled;
        _identityProvider =
            identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.ApplyProcessEcoQos);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<RuntimeProcessEcoQosState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        RuntimeProcessEcoQosState original =
            await ReadCurrentStateCoreAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!original.IsRunning
            || original.ExecutionSpeedThrottled is null)
        {
            throw new InvalidOperationException(
                "The optional background process is no longer running.");
        }

        return new(
            original,
            new(
                IsRunning: true,
                ExecutionSpeedThrottled: _desiredEnabled),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeProcessEcoQosState current =
                await ReadCurrentStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            return current == preparedAction.OriginalState
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "The process EcoQoS state changed after preparation.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionValidationResult.Blocked(exception.Message);
        }
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        ProcessPowerThrottlingController.Set(
            process,
            _desiredEnabled);
        if (_desiredEnabled)
        {
            _ = ProcessMemoryTrimmer.TryTrimWorkingSet(process, out _);
        }
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken) =>
        VerifyStateAsync(
            preparedAction.DesiredState,
            allowMissingProcess: false,
            cancellationToken);

    public async ValueTask<RuntimeProcessEcoQosState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken) =>
        await ReadCurrentStateCoreAsync(cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<RuntimeProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        RuntimeProcessEcoQosState current =
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
        ProcessPowerThrottlingController.Set(
            process,
            preparedAction.OriginalState.ExecutionSpeedThrottled
                ?? throw new InvalidDataException(
                    "The original EcoQoS state is missing."));
    }

    public ValueTask<ActionVerificationResult>
        VerifyCompensationAsync(
            ActionExecutionContext context,
            PreparedAction<RuntimeProcessEcoQosState> preparedAction,
            CancellationToken cancellationToken) =>
        VerifyStateAsync(
            preparedAction.OriginalState,
            allowMissingProcess: true,
            cancellationToken);

    public RestoreDecision<RuntimeProcessEcoQosState> DecideRecovery(
        RuntimeProcessEcoQosState originalState,
        RuntimeProcessEcoQosState appliedState,
        RuntimeProcessEcoQosState currentState) =>
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

    private async ValueTask<RuntimeProcessEcoQosState>
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
            return new(
                IsRunning: false,
                ExecutionSpeedThrottled: null);
        }

        using Process process =
            await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        ProcessEcoQosState state =
            ProcessPowerThrottlingController.Read(process);
        return new(
            IsRunning: true,
            state.ExecutionSpeedThrottled);
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        RuntimeProcessEcoQosState expected,
        bool allowMissingProcess,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeProcessEcoQosState current =
                await ReadCurrentStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (allowMissingProcess && !current.IsRunning)
            {
                return ActionVerificationResult.Verified();
            }

            return current == expected
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    "The process EcoQoS state does not match the expected state.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}

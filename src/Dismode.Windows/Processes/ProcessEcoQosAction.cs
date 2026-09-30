using System.ComponentModel;
using System.Diagnostics;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed class ProcessEcoQosAction :
    IReversibleAction<ProcessEcoQosState>
{
    private readonly ProcessIdentity _expectedIdentity;
    private readonly IProcessIdentityProvider _identityProvider;

    public ProcessEcoQosAction(
        ActionId actionId,
        ProcessIdentity expectedIdentity,
        IProcessIdentityProvider? identityProvider = null)
    {
        _expectedIdentity = expectedIdentity;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        Descriptor = new(
            actionId,
            SystemTargetKind.Process,
            expectedIdentity.RuntimeKey.ToString(),
            OptimizationActionKind.ApplyProcessEcoQos);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<ProcessEcoQosState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        ProcessEcoQosState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return new(
            originalState,
            new ProcessEcoQosState(ExecutionSpeedThrottled: true),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessEcoQosState currentState =
                await ReadCurrentStateAsync(context, cancellationToken)
                    .ConfigureAwait(false);
            return currentState == preparedAction.OriginalState
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
        PreparedAction<ProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        ProcessPowerThrottlingController.Set(
            process,
            preparedAction.DesiredState.ExecutionSpeedThrottled);
        if (preparedAction.DesiredState.ExecutionSpeedThrottled)
        {
            _ = ProcessMemoryTrimmer.TryTrimWorkingSet(process, out _);
        }
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<ProcessEcoQosState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        return ProcessPowerThrottlingController.Read(process);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken)
    {
        using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                _expectedIdentity,
                _identityProvider,
                cancellationToken)
            .ConfigureAwait(false);
        ProcessPowerThrottlingController.Set(
            process,
            preparedAction.OriginalState.ExecutionSpeedThrottled);
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<ProcessEcoQosState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ProcessEcoQosState expectedState,
        CancellationToken cancellationToken)
    {
        try
        {
            using Process process = await ProcessTargetGuard.OpenValidatedAsync(
                    _expectedIdentity,
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            ProcessEcoQosState currentState =
                ProcessPowerThrottlingController.Read(process);
            return currentState == expectedState
                ? ActionVerificationResult.Verified()
                : ActionVerificationResult.Failed(
                    "The observed EcoQoS state does not match the expected state.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return ActionVerificationResult.Failed(exception.Message);
        }
    }
}

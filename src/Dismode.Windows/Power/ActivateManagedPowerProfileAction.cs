using Dismode.Core.Actions;
using Dismode.Core.Devices;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Policies;
using Dismode.Core.Recovery;
using Dismode.Windows.Devices;

namespace Dismode.Windows.Power;

public sealed class ActivateManagedPowerProfileAction :
    IReversibleAction<PowerSchemeActionState>,
    IRecoveryDecisionProvider<PowerSchemeActionState>
{
    private readonly Guid _managedSchemeId;
    private readonly RecoveryAssurance _recoveryAssurance;
    private readonly bool _isOwnedByDismode;
    private readonly IPowerSchemeAdapter _adapter;
    private readonly IDeviceFormFactorDetector _formFactorDetector;
    private readonly TimeProvider _timeProvider;

    public ActivateManagedPowerProfileAction(
        ActionId actionId,
        Guid managedSchemeId,
        RecoveryAssurance recoveryAssurance,
        bool isOwnedByDismode,
        IPowerSchemeAdapter? adapter = null,
        IDeviceFormFactorDetector? formFactorDetector = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(
            managedSchemeId,
            Guid.Empty);

        _managedSchemeId = managedSchemeId;
        _recoveryAssurance = recoveryAssurance;
        _isOwnedByDismode = isOwnedByDismode;
        _adapter = adapter ?? new WindowsPowerSchemeAdapter();
        _formFactorDetector = formFactorDetector ?? new WindowsDeviceFormFactorDetector();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new(
            actionId,
            SystemTargetKind.PowerProfile,
            managedSchemeId.ToString("D"),
            OptimizationActionKind.ActivateManagedPowerProfile);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<PowerSchemeActionState>> PrepareAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        PowerSchemeActionState originalState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        PowerSchemeActionState desiredState = new(
            _managedSchemeId,
            ManagedSchemeExists: true);

        return new(
            originalState,
            desiredState,
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<PowerSchemeActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        if (!HasExpectedDesiredState(preparedAction))
        {
            return ActionValidationResult.Blocked(
                "The managed power scheme plan is inconsistent.");
        }

        PowerSchemeActionState currentState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        if (currentState != preparedAction.OriginalState)
        {
            return ActionValidationResult.Blocked(
                "The active power scheme changed after preparation.");
        }

        if (preparedAction.OriginalState.ManagedSchemeExists)
        {
            return ActionValidationResult.Blocked(
                "The requested managed power scheme identifier already exists.");
        }

        if (preparedAction.OriginalState.ActiveSchemeId == _managedSchemeId)
        {
            return ActionValidationResult.Blocked(
                "The managed power scheme cannot also be the original scheme.");
        }

        DeviceFormFactor formFactor = _formFactorDetector.Detect();
        SafetyDecision safety = HardSafetyPolicy.Evaluate(
            new(
                Descriptor.TargetId,
                OptimizationActionKind.ActivateManagedPowerProfile,
                TargetProtection.None,
                _recoveryAssurance,
                isApprovedTarget: false,
                _isOwnedByDismode,
                formFactor));

        return safety.IsAllowed
            ? ActionValidationResult.Allowed()
            : ActionValidationResult.Blocked(safety.Explanation);
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<PowerSchemeActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        ActionValidationResult validation =
            await ValidateAsync(context, preparedAction, cancellationToken)
                .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                validation.BlockingReason
                ?? "The managed power scheme activation was rejected.");
        }

        await _adapter
            .DuplicateSchemeAsync(
                preparedAction.OriginalState.ActiveSchemeId,
                _managedSchemeId,
                cancellationToken)
            .ConfigureAwait(false);

        Guid activeAfterDuplicate =
            await _adapter
                .GetActiveSchemeAsync(cancellationToken)
                .ConfigureAwait(false);
        if (activeAfterDuplicate
            != preparedAction.OriginalState.ActiveSchemeId)
        {
            throw new InvalidOperationException(
                "The active power scheme changed while applying the plan.");
        }

        await _adapter
            .SetActiveSchemeAsync(_managedSchemeId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<PowerSchemeActionState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                context,
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<PowerSchemeActionState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Guid activeSchemeId =
            await _adapter
                .GetActiveSchemeAsync(cancellationToken)
                .ConfigureAwait(false);
        bool managedSchemeExists =
            await _adapter
                .SchemeExistsAsync(_managedSchemeId, cancellationToken)
                .ConfigureAwait(false);
        return new(activeSchemeId, managedSchemeExists);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<PowerSchemeActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        PowerSchemeActionState currentState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);

        if (currentState.ActiveSchemeId != _managedSchemeId
            && currentState.ActiveSchemeId
                != preparedAction.OriginalState.ActiveSchemeId)
        {
            throw new InvalidOperationException(
                "A different power scheme is active; the external choice was preserved.");
        }

        if (currentState.ActiveSchemeId == _managedSchemeId)
        {
            await _adapter
                .SetActiveSchemeAsync(
                    preparedAction.OriginalState.ActiveSchemeId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (currentState.ManagedSchemeExists)
        {
            await _adapter
                .DeleteSchemeAsync(_managedSchemeId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<PowerSchemeActionState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                context,
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    public RestoreDecision<PowerSchemeActionState> DecideRecovery(
        PowerSchemeActionState originalState,
        PowerSchemeActionState appliedState,
        PowerSchemeActionState currentState)
    {
        RestoreDecisionKind kind;

        if (currentState == originalState)
        {
            kind = RestoreDecisionKind.NoActionAlreadyOriginal;
        }
        else if (currentState.ManagedSchemeExists
            && (currentState.ActiveSchemeId == originalState.ActiveSchemeId
                || currentState.ActiveSchemeId == appliedState.ActiveSchemeId))
        {
            kind = RestoreDecisionKind.RestoreOriginal;
        }
        else
        {
            kind = RestoreDecisionKind.PreserveCurrentAndReportConflict;
        }

        return new(kind, originalState, appliedState, currentState);
    }

    private bool HasExpectedDesiredState(
        PreparedAction<PowerSchemeActionState> preparedAction) =>
        preparedAction.DesiredState
            == new PowerSchemeActionState(
                _managedSchemeId,
                ManagedSchemeExists: true);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        ActionExecutionContext context,
        PowerSchemeActionState expectedState,
        CancellationToken cancellationToken)
    {
        PowerSchemeActionState currentState =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return currentState == expectedState
            ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed(
                "The active or managed power scheme does not match the expected state.");
    }
}

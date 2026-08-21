using System.ServiceProcess;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Policies;
using GameShift.Core.Services;

namespace GameShift.Windows.Services;

public sealed class StopApprovedServiceAction :
    IReversibleAction<WindowsServiceActionState>
{
    private readonly string _serviceName;
    private readonly ServiceStopApproval _approval;
    private readonly RecoveryAssurance _recoveryAssurance;
    private readonly IWindowsServiceControlAdapter _adapter;
    private readonly TimeProvider _timeProvider;

    public StopApprovedServiceAction(
        ActionId actionId,
        string serviceName,
        ServiceStopApproval approval,
        RecoveryAssurance recoveryAssurance,
        IWindowsServiceControlAdapter? adapter = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _serviceName = serviceName.Trim();
        _approval = approval;
        _recoveryAssurance = recoveryAssurance;
        _adapter = adapter ?? new WindowsServiceControlAdapter();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Descriptor = new(
            actionId,
            SystemTargetKind.WindowsService,
            _serviceName,
            OptimizationActionKind.StopApprovedService);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<WindowsServiceActionState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot snapshot =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        WindowsServiceActionState original = ToState(snapshot);
        return new(
            original,
            new(
                ServiceControllerStatus.Stopped,
                original.ConfigurationFingerprint),
            _timeProvider.GetUtcNow());
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot snapshot =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        return Evaluate(context, preparedAction, snapshot);
    }

    public async ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot snapshot =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        ActionValidationResult validation =
            Evaluate(context, preparedAction, snapshot);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                validation.BlockingReason
                ?? "The service stop was rejected.");
        }

        await _adapter
            .RequestStopAsync(_serviceName, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<WindowsServiceActionState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot snapshot =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        return ToState(snapshot);
    }

    public async ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot current =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                current.ConfigurationFingerprint,
                preparedAction.OriginalState.ConfigurationFingerprint))
        {
            throw new InvalidOperationException(
                "The service configuration changed; automatic restore was cancelled.");
        }

        if (current.Status != ServiceControllerStatus.Running)
        {
            await _adapter
                .RequestStartAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    private ActionValidationResult Evaluate(
        ActionExecutionContext context,
        PreparedAction<WindowsServiceActionState> preparedAction,
        WindowsServiceSnapshot snapshot)
    {
        if (!_approval.IsValidFor(
                _serviceName,
                context.SessionId,
                _timeProvider.GetUtcNow()))
        {
            return ActionValidationResult.Blocked(
                "The explicit service approval is missing or expired.");
        }

        WindowsServiceActionState current = ToState(snapshot);
        if (current != preparedAction.OriginalState)
        {
            return ActionValidationResult.Blocked(
                "The service state or configuration changed after preparation.");
        }

        TargetProtection protection =
            ProtectedServiceCatalog.Classify(_serviceName);
        if (snapshot.IsDriver)
        {
            protection |= TargetProtection.Driver;
        }

        SafetyDecision safety = HardSafetyPolicy.Evaluate(
            new(
                _serviceName,
                OptimizationActionKind.StopApprovedService,
                protection,
                _recoveryAssurance,
                isApprovedTarget: true,
                isOwnedByGameShift: false));
        if (!safety.IsAllowed)
        {
            return ActionValidationResult.Blocked(safety.Explanation);
        }

        if (snapshot.Status != ServiceControllerStatus.Running)
        {
            return ActionValidationResult.Blocked(
                "Only a running service can be selected for temporary stop.");
        }

        if (!snapshot.CanStop)
        {
            return ActionValidationResult.Blocked(
                "The service does not accept a stop request.");
        }

        if (snapshot.IsSharedProcess)
        {
            return ActionValidationResult.Blocked(
                "Safe mode does not stop a service hosted in a shared process.");
        }

        if (snapshot.TriggerCount > 0)
        {
            return ActionValidationResult.Blocked(
                "Safe mode does not stop a trigger-start service.");
        }

        if (snapshot.RunningDependentServices.Count > 0)
        {
            return ActionValidationResult.Blocked(
                "One or more running services depend on this service.");
        }

        if (snapshot.StartType == ServiceStartMode.Disabled)
        {
            return ActionValidationResult.Blocked(
                "A disabled service cannot be safely restarted.");
        }

        return ActionValidationResult.Allowed();
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        WindowsServiceActionState expected,
        CancellationToken cancellationToken)
    {
        WindowsServiceSnapshot current =
            await _adapter
                .CaptureAsync(_serviceName, cancellationToken)
                .ConfigureAwait(false);
        return ToState(current) == expected
            ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed(
                "The service state or configuration does not match the expected state.");
    }

    private static WindowsServiceActionState ToState(
        WindowsServiceSnapshot snapshot) =>
        new(snapshot.Status, snapshot.ConfigurationFingerprint);
}

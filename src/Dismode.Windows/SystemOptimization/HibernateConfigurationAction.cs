using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Recovery;

namespace Dismode.Windows.SystemOptimization;

public sealed class HibernateConfigurationAction :
    IReversibleAction<HibernateConfigurationState>,
    IRecoveryDecisionProvider<HibernateConfigurationState>
{
    private readonly bool _desiredEnabled;
    private readonly IHibernateConfigurationAdapter _adapter;

    public HibernateConfigurationAction(
        ActionId actionId,
        bool desiredEnabled,
        IHibernateConfigurationAdapter? adapter = null)
    {
        _desiredEnabled = desiredEnabled;
        _adapter = adapter ?? new WindowsHibernateConfigurationAdapter();
        Descriptor = new(
            actionId,
            SystemTargetKind.OperatingSystemSetting,
            "windows.hibernate",
            OptimizationActionKind.ConfigureHibernation);
    }

    public ActionDescriptor Descriptor { get; }

    public async ValueTask<PreparedAction<HibernateConfigurationState>>
        PrepareAsync(
            ActionExecutionContext context,
            CancellationToken cancellationToken)
    {
        HibernateConfigurationState original =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        if (!original.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Windows did not expose a verifiable hibernation state.");
        }

        return new(
            original,
            new(_desiredEnabled, IsSupported: true),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(
        ActionExecutionContext context,
        PreparedAction<HibernateConfigurationState> preparedAction,
        CancellationToken cancellationToken)
    {
        HibernateConfigurationState current =
            await ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);
        return current == preparedAction.OriginalState
            && current.IsSupported
            && preparedAction.DesiredState
                == new HibernateConfigurationState(
                    _desiredEnabled,
                    IsSupported: true)
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked(
                    "The hibernation capability or current state changed after snapshot.");
    }

    public ValueTask ApplyAsync(
        ActionExecutionContext context,
        PreparedAction<HibernateConfigurationState> preparedAction,
        CancellationToken cancellationToken) =>
        _adapter.SetAsync(
            preparedAction.DesiredState.Enabled,
            cancellationToken);

    public async ValueTask<ActionVerificationResult> VerifyAsync(
        ActionExecutionContext context,
        PreparedAction<HibernateConfigurationState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.DesiredState,
                cancellationToken)
            .ConfigureAwait(false);

    public ValueTask<HibernateConfigurationState> ReadCurrentStateAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken) =>
        _adapter.ReadAsync(cancellationToken);

    public ValueTask CompensateAsync(
        ActionExecutionContext context,
        PreparedAction<HibernateConfigurationState> preparedAction,
        CancellationToken cancellationToken) =>
        _adapter.SetAsync(
            preparedAction.OriginalState.Enabled,
            cancellationToken);

    public async ValueTask<ActionVerificationResult> VerifyCompensationAsync(
        ActionExecutionContext context,
        PreparedAction<HibernateConfigurationState> preparedAction,
        CancellationToken cancellationToken) =>
        await VerifyStateAsync(
                preparedAction.OriginalState,
                cancellationToken)
            .ConfigureAwait(false);

    public RestoreDecision<HibernateConfigurationState> DecideRecovery(
        HibernateConfigurationState originalState,
        HibernateConfigurationState appliedState,
        HibernateConfigurationState currentState) =>
        ThreeWayReconciler.Decide(
            originalState,
            appliedState,
            currentState);

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(
        HibernateConfigurationState expected,
        CancellationToken cancellationToken)
    {
        HibernateConfigurationState current =
            await _adapter.ReadAsync(cancellationToken).ConfigureAwait(false);
        return current == expected
            ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed(
                "The independently read hibernation state does not match the expected value.");
    }
}

public sealed record HibernateConfigurationState(
    bool Enabled,
    bool IsSupported);

public interface IHibernateConfigurationAdapter
{
    ValueTask<HibernateConfigurationState> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask SetAsync(
        bool enabled,
        CancellationToken cancellationToken);
}

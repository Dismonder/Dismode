using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Recovery;
using Dismode.Windows.Cpu;

namespace Dismode.Windows.Processes;

internal sealed record ProcessCpuSetsState(bool IsRunning, bool QuerySucceeded, uint[] Ids);

internal sealed class ProcessCpuSetsAction : IReversibleAction<ProcessCpuSetsState>,
    IRecoveryDecisionProvider<ProcessCpuSetsState>
{
    private readonly ProcessIdentity _identity;
    private readonly uint[] _target;
    private readonly IProcessIdentityProvider _identities;
    private readonly ICpuSetsPort _port;

    internal ProcessCpuSetsAction(ActionId actionId, ProcessIdentity identity, IReadOnlyList<uint> target,
        IProcessIdentityProvider identities, ICpuSetsPort port)
    {
        _identity = identity;
        _target = [.. target.Distinct().Order()];
        _identities = identities;
        _port = port;
        Descriptor = new(actionId, SystemTargetKind.Process, identity.RuntimeKey.ToString(),
            OptimizationActionKind.AssignProcessCpuSets);
    }

    public ActionDescriptor Descriptor { get; }

    internal static async ValueTask<ActionRecoveryResult> RestoreLegacyAsync(ProcessIdentity identity,
        uint[] originalIds, uint[] appliedIds, IProcessIdentityProvider identities, ICpuSetsPort port,
        CancellationToken cancellationToken)
    {
        ProcessCpuSetsAction action = new(ActionId.Create(), identity, appliedIds, identities, port);
        ActionExecutionContext context = new(SessionId.Create(), action.Descriptor.ActionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(), DateTimeOffset.UtcNow);
        PreparedAction<ProcessCpuSetsState> prepared = new(new(true, true, originalIds),
            new(true, true, appliedIds), DateTimeOffset.UtcNow);
        ProcessCpuSetsState current = await action.ReadCurrentStateAsync(context, cancellationToken).ConfigureAwait(false);
        RestoreDecision<ProcessCpuSetsState> decision = action.DecideRecovery(prepared.OriginalState, prepared.DesiredState, current);
        if (decision.Kind == RestoreDecisionKind.PreserveCurrentAndReportConflict)
        {
            return new(ActionRecoveryStatus.ConflictRequiresDecision, "External CPU Sets assignment was preserved.");
        }

        if (decision.Kind == RestoreDecisionKind.RestoreOriginal)
        {
            await action.CompensateAsync(context, prepared, cancellationToken).ConfigureAwait(false);
            ActionVerificationResult verification = await action.VerifyCompensationAsync(context, prepared, cancellationToken).ConfigureAwait(false);
            if (!verification.MatchesExpectedState)
            {
                return new(ActionRecoveryStatus.VerificationFailed, verification.Details);
            }
        }

        return new(ActionRecoveryStatus.Restored, null);
    }

    public async ValueTask<PreparedAction<ProcessCpuSetsState>> PrepareAsync(
        ActionExecutionContext context, CancellationToken cancellationToken)
    {
        ProcessCpuSetsState original = await ReadCurrentStateAsync(context, cancellationToken).ConfigureAwait(false);
        ProcessCpuSetsState desired = new(true, true, _target);
        return new(original, desired, DateTimeOffset.UtcNow)
        {
            IsUnchanged = StateComparer.Instance.Equals(original, desired),
        };
    }

    public async ValueTask<ActionValidationResult> ValidateAsync(ActionExecutionContext context,
        PreparedAction<ProcessCpuSetsState> preparedAction, CancellationToken cancellationToken)
    {
        ProcessCpuSetsState current = await ReadCurrentStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!current.IsRunning || !current.QuerySucceeded
            || !StateComparer.Instance.Equals(current, preparedAction.OriginalState))
        {
            return ActionValidationResult.Blocked("CPU Sets state is unavailable or changed after preparation.");
        }

        return StateComparer.Instance.Equals(current, preparedAction.DesiredState)
            || (current.Ids.Length == 0 && _target.Length > 0)
                ? ActionValidationResult.Allowed()
                : ActionValidationResult.Blocked("Foreign CPU Sets assignment was preserved.");
    }

    public async ValueTask ApplyAsync(ActionExecutionContext context,
        PreparedAction<ProcessCpuSetsState> preparedAction, CancellationToken cancellationToken)
    {
        ActionValidationResult validation = await ValidateAsync(context, preparedAction, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || !_port.Apply(_identity.RuntimeKey.ProcessId, preparedAction.DesiredState.Ids))
        {
            throw new InvalidOperationException(validation.BlockingReason ?? "CPU Sets write failed.");
        }
    }

    public async ValueTask<ProcessCpuSetsState> ReadCurrentStateAsync(
        ActionExecutionContext context, CancellationToken cancellationToken)
    {
        if (!await _identities.MatchesRuntimeIdentityAsync(_identity, cancellationToken).ConfigureAwait(false))
        {
            return new(false, true, []);
        }

        CpuSetsQuery query = _port.Query(_identity.RuntimeKey.ProcessId);
        return new(true, query.State != CpuSetsQueryState.Failed, [.. query.Ids.Distinct().Order()]);
    }

    public ValueTask<ActionVerificationResult> VerifyAsync(ActionExecutionContext context,
        PreparedAction<ProcessCpuSetsState> preparedAction, CancellationToken cancellationToken) =>
        VerifyStateAsync(context, preparedAction.DesiredState, false, cancellationToken);

    public async ValueTask CompensateAsync(ActionExecutionContext context,
        PreparedAction<ProcessCpuSetsState> preparedAction, CancellationToken cancellationToken)
    {
        ProcessCpuSetsState current = await ReadCurrentStateAsync(context, cancellationToken).ConfigureAwait(false);
        RestoreDecision<ProcessCpuSetsState> decision = DecideRecovery(
            preparedAction.OriginalState, preparedAction.DesiredState, current);
        if (decision.Kind == RestoreDecisionKind.PreserveCurrentAndReportConflict)
        {
            throw new InvalidOperationException("CPU Sets changed before compensation.");
        }

        if (decision.Kind == RestoreDecisionKind.RestoreOriginal
            && !_port.Apply(_identity.RuntimeKey.ProcessId, preparedAction.OriginalState.Ids))
        {
            throw new InvalidOperationException("CPU Sets restore failed.");
        }
    }

    public ValueTask<ActionVerificationResult> VerifyCompensationAsync(ActionExecutionContext context,
        PreparedAction<ProcessCpuSetsState> preparedAction, CancellationToken cancellationToken) =>
        VerifyStateAsync(context, preparedAction.OriginalState, true, cancellationToken);

    public RestoreDecision<ProcessCpuSetsState> DecideRecovery(ProcessCpuSetsState originalState,
        ProcessCpuSetsState appliedState, ProcessCpuSetsState currentState)
    {
        if (!currentState.QuerySucceeded)
        {
            throw new InvalidOperationException("CPU Sets query failed during recovery.");
        }

        return !currentState.IsRunning
            ? new(RestoreDecisionKind.NoActionAlreadyOriginal, originalState, appliedState, currentState)
            : ThreeWayReconciler.Decide(originalState, appliedState, currentState, StateComparer.Instance);
    }

    private async ValueTask<ActionVerificationResult> VerifyStateAsync(ActionExecutionContext context,
        ProcessCpuSetsState expected, bool allowMissing, CancellationToken cancellationToken)
    {
        ProcessCpuSetsState current = await ReadCurrentStateAsync(context, cancellationToken).ConfigureAwait(false);
        return (allowMissing && !current.IsRunning) || StateComparer.Instance.Equals(current, expected)
            ? ActionVerificationResult.Verified()
            : ActionVerificationResult.Failed("CPU Sets read-back does not match the expected assignment.");
    }

    private sealed class StateComparer : IEqualityComparer<ProcessCpuSetsState>
    {
        internal static StateComparer Instance { get; } = new();
        public bool Equals(ProcessCpuSetsState? x, ProcessCpuSetsState? y) =>
            x is not null && y is not null && x.IsRunning == y.IsRunning
            && x.QuerySucceeded == y.QuerySucceeded && x.Ids.ToHashSet().SetEquals(y.Ids);
        public int GetHashCode(ProcessCpuSetsState obj) => obj.IsRunning.GetHashCode();
    }
}

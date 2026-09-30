using System.Text.Json;
using Dismode.Core.Actions;
using Dismode.Core.Journal;
using Dismode.Core.Transactions;

namespace Dismode.Core.Recovery;

public sealed class ActionRecoveryCoordinator<TState>
    where TState : notnull
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.General);

    private readonly IRecoveryJournal _journal;
    private readonly IExecutionCheckpointObserver _checkpointObserver;

    public ActionRecoveryCoordinator(
        IRecoveryJournal journal,
        IExecutionCheckpointObserver? checkpointObserver = null)
    {
        _journal = journal;
        _checkpointObserver =
            checkpointObserver ?? NoOpExecutionCheckpointObserver.Instance;
    }

    public async ValueTask<ActionRecoveryResult> RecoverAsync(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (action.Descriptor.ActionId != context.ActionId)
        {
            throw new InvalidOperationException(
                "The action descriptor does not match the recovery context.");
        }

        IReadOnlyList<RecoveryJournalEntry> records =
            await ReadActionRecordsAsync(context, cancellationToken).ConfigureAwait(false);

        if (records.Any(record =>
                record.EventKind == JournalEventKind.CompensationVerified))
        {
            return new(ActionRecoveryStatus.AlreadyRestored, null);
        }

        RecoveryJournalEntry? preparedRecord = records.LastOrDefault(record =>
            record.EventKind == JournalEventKind.ActionPrepared);

        if (preparedRecord is null
            || preparedRecord.OriginalStateJson is null
            || preparedRecord.DesiredStateJson is null)
        {
            return new(
                ActionRecoveryStatus.MissingPreparation,
                "The durable preparation snapshot is missing.");
        }

        bool mutationMayHaveStarted = records.Any(record =>
            record.EventKind is
                JournalEventKind.ActionApplying
                or JournalEventKind.ActionApplied
                or JournalEventKind.ActionVerified
                or JournalEventKind.ActionVerificationFailed
                or JournalEventKind.ActionApplyFailed
                or JournalEventKind.ActionCompensating
                or JournalEventKind.ActionCompensated
                or JournalEventKind.CompensationVerificationFailed);

        if (!mutationMayHaveStarted)
        {
            return new(ActionRecoveryStatus.NotRequired, null);
        }

        TState originalState = Deserialize(preparedRecord.OriginalStateJson);
        TState desiredState = Deserialize(preparedRecord.DesiredStateJson);
        PreparedAction<TState> preparedAction = new(
            originalState,
            desiredState,
            preparedRecord.TimestampUtc);
        TState currentState =
            await action.ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);

        RestoreDecision<TState> decision =
            action is IRecoveryDecisionProvider<TState> decisionProvider
                ? decisionProvider.DecideRecovery(
                    originalState,
                    desiredState,
                    currentState)
                : ThreeWayReconciler.Decide(
                    originalState,
                    desiredState,
                    currentState);

        if (decision.Kind == RestoreDecisionKind.PreserveCurrentAndReportConflict)
        {
            await AppendAsync(
                    action,
                    context,
                    JournalEventKind.ExternalConflictDetected,
                    preparedAction,
                    currentState,
                    "The current state differs from both the original and applied states.",
                    cancellationToken)
                .ConfigureAwait(false);

            return new(
                ActionRecoveryStatus.ConflictRequiresDecision,
                "An external change was preserved.");
        }

        if (decision.Kind == RestoreDecisionKind.NoActionAlreadyOriginal)
        {
            await AppendAsync(
                    action,
                    context,
                    JournalEventKind.CompensationVerified,
                    preparedAction,
                    currentState,
                    "The target was already in its original state.",
                    cancellationToken)
                .ConfigureAwait(false);

            return new(ActionRecoveryStatus.Restored, null);
        }

        await AppendAsync(
                action,
                context,
                JournalEventKind.RestoreStarted,
                preparedAction,
                currentState,
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.BeforeCompensation,
                cancellationToken)
            .ConfigureAwait(false);

        await AppendAsync(
                action,
                context,
                JournalEventKind.ActionCompensating,
                preparedAction,
                currentState,
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await action.CompensateAsync(context, preparedAction, cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterCompensationBeforeJournal,
                cancellationToken)
            .ConfigureAwait(false);

        TState compensatedState =
            await action.ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);

        await AppendAsync(
                action,
                context,
                JournalEventKind.ActionCompensated,
                preparedAction,
                compensatedState,
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterCompensationJournaled,
                cancellationToken)
            .ConfigureAwait(false);

        ActionVerificationResult verification =
            await action
                .VerifyCompensationAsync(context, preparedAction, cancellationToken)
                .ConfigureAwait(false);

        await AppendAsync(
                action,
                context,
                verification.MatchesExpectedState
                    ? JournalEventKind.CompensationVerified
                    : JournalEventKind.CompensationVerificationFailed,
                preparedAction,
                compensatedState,
                verification.Details,
                cancellationToken)
            .ConfigureAwait(false);

        return verification.MatchesExpectedState
            ? new(ActionRecoveryStatus.Restored, null)
            : new(ActionRecoveryStatus.VerificationFailed, verification.Details);
    }

    private async ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadActionRecordsAsync(
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RecoveryJournalEntry> allRecords =
            await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        return allRecords
            .Where(record =>
                record.SessionId == context.SessionId.Value
                && record.ActionId == context.ActionId.Value
                && record.IdempotencyKey == context.IdempotencyKey.Value)
            .OrderBy(record => record.Sequence)
            .ToArray();
    }

    private ValueTask<RecoveryJournalEntry> AppendAsync(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        JournalEventKind eventKind,
        PreparedAction<TState> preparedAction,
        TState currentState,
        string? details,
        CancellationToken cancellationToken)
    {
        RecoveryJournalDraft draft = new(
            context.SessionId.Value,
            context.ActionId.Value,
            context.IdempotencyKey.Value,
            eventKind,
            action.Descriptor.TargetKind.ToString(),
            action.Descriptor.TargetId,
            Serialize(preparedAction.OriginalState),
            Serialize(preparedAction.DesiredState),
            Serialize(currentState),
            SessionCheckpoint: null,
            details,
            DateTimeOffset.UtcNow);

        return _journal.AppendDurableAsync(draft, cancellationToken);
    }

    private static string Serialize(TState state) =>
        JsonSerializer.Serialize(state, SerializerOptions);

    private static TState Deserialize(string json) =>
        JsonSerializer.Deserialize<TState>(json, SerializerOptions)
        ?? throw new InvalidDataException("The journal state payload was null.");
}

using System.Text.Json;
using GameShift.Core.Actions;
using GameShift.Core.Journal;

namespace GameShift.Core.Transactions;

public sealed class TransactionCoordinator<TState>
    where TState : notnull
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.General);

    private readonly IRecoveryJournal _journal;
    private readonly IExecutionCheckpointObserver _checkpointObserver;

    public TransactionCoordinator(
        IRecoveryJournal journal,
        IExecutionCheckpointObserver? checkpointObserver = null)
    {
        _journal = journal;
        _checkpointObserver =
            checkpointObserver ?? NoOpExecutionCheckpointObserver.Instance;
    }

    public async ValueTask<ActionExecutionResult> ExecuteAsync(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        EnsureMatchingAction(action, context);

        IReadOnlyList<RecoveryJournalEntry> records =
            await ReadActionRecordsAsync(context, cancellationToken).ConfigureAwait(false);
        ActionExecutionResult? replayResult = ClassifyReplay(records);

        if (replayResult is not null)
        {
            return replayResult;
        }

        PreparedAction<TState> preparedAction =
            await action.PrepareAsync(context, cancellationToken).ConfigureAwait(false);

        await AppendAsync(
                action,
                context,
                JournalEventKind.ActionPrepared,
                originalStateJson: Serialize(preparedAction.OriginalState),
                desiredStateJson: Serialize(preparedAction.DesiredState),
                currentStateJson: null,
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterPreparationJournaled,
                cancellationToken)
            .ConfigureAwait(false);

        ActionValidationResult validation =
            await action
                .ValidateAsync(context, preparedAction, cancellationToken)
                .ConfigureAwait(false);

        if (!validation.IsValid)
        {
            await AppendAsync(
                    action,
                    context,
                    JournalEventKind.ActionBlocked,
                    Serialize(preparedAction.OriginalState),
                    Serialize(preparedAction.DesiredState),
                    currentStateJson: null,
                    validation.BlockingReason,
                    cancellationToken)
                .ConfigureAwait(false);

            return new(ActionExecutionStatus.Blocked, validation.BlockingReason);
        }

        await AppendAsync(
                action,
                context,
                JournalEventKind.ActionApplying,
                Serialize(preparedAction.OriginalState),
                Serialize(preparedAction.DesiredState),
                currentStateJson: null,
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(ExecutionCheckpoint.BeforeApply, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await action.ApplyAsync(context, preparedAction, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await AppendAsync(
                    action,
                    context,
                    JournalEventKind.ActionApplyFailed,
                    Serialize(preparedAction.OriginalState),
                    Serialize(preparedAction.DesiredState),
                    currentStateJson: null,
                    exception.GetType().Name,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterApplyBeforeAppliedJournal,
                cancellationToken)
            .ConfigureAwait(false);

        TState currentState =
            await action.ReadCurrentStateAsync(context, cancellationToken)
                .ConfigureAwait(false);

        await AppendAsync(
                action,
                context,
                JournalEventKind.ActionApplied,
                Serialize(preparedAction.OriginalState),
                Serialize(preparedAction.DesiredState),
                Serialize(currentState),
                details: null,
                cancellationToken)
            .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterAppliedJournaled,
                cancellationToken)
            .ConfigureAwait(false);

        ActionVerificationResult verification =
            await action.VerifyAsync(context, preparedAction, cancellationToken)
                .ConfigureAwait(false);

        await _checkpointObserver
            .OnCheckpointAsync(
                ExecutionCheckpoint.AfterVerificationBeforeJournal,
                cancellationToken)
            .ConfigureAwait(false);

        JournalEventKind verificationEvent = verification.MatchesExpectedState
            ? JournalEventKind.ActionVerified
            : JournalEventKind.ActionVerificationFailed;

        await AppendAsync(
                action,
                context,
                verificationEvent,
                Serialize(preparedAction.OriginalState),
                Serialize(preparedAction.DesiredState),
                Serialize(currentState),
                verification.Details,
                cancellationToken)
            .ConfigureAwait(false);

        return verification.MatchesExpectedState
            ? new(ActionExecutionStatus.AppliedAndVerified, null)
            : new(ActionExecutionStatus.VerificationFailed, verification.Details);
    }

    private static void EnsureMatchingAction(
        IReversibleAction<TState> action,
        ActionExecutionContext context)
    {
        if (action.Descriptor.ActionId != context.ActionId)
        {
            throw new InvalidOperationException(
                "The action descriptor does not match the execution context.");
        }
    }

    private static ActionExecutionResult? ClassifyReplay(
        IReadOnlyList<RecoveryJournalEntry> records)
    {
        if (records.Any(record =>
                record.EventKind == JournalEventKind.ActionVerified))
        {
            return new(ActionExecutionStatus.AlreadyCompleted, null);
        }

        if (records.Any(record =>
                record.EventKind is
                    JournalEventKind.ActionApplying
                    or JournalEventKind.ActionApplied
                    or JournalEventKind.ActionVerificationFailed
                    or JournalEventKind.ActionApplyFailed))
        {
            return new(
                ActionExecutionStatus.RecoveryRequired,
                "A previous attempt may have changed the target and must be reconciled.");
        }

        if (records.Any(record =>
                record.EventKind == JournalEventKind.ActionBlocked))
        {
            return new(
                ActionExecutionStatus.Blocked,
                records[^1].Details);
        }

        return null;
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
        string? originalStateJson,
        string? desiredStateJson,
        string? currentStateJson,
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
            originalStateJson,
            desiredStateJson,
            currentStateJson,
            SessionCheckpoint: null,
            details,
            DateTimeOffset.UtcNow);

        return _journal.AppendDurableAsync(draft, cancellationToken);
    }

    private static string Serialize(TState state) =>
        JsonSerializer.Serialize(state, SerializerOptions);
}

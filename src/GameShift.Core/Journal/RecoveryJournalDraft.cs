namespace GameShift.Core.Journal;

public sealed record RecoveryJournalDraft(
    Guid SessionId,
    Guid? ActionId,
    Guid? IdempotencyKey,
    JournalEventKind EventKind,
    string TargetKind,
    string TargetId,
    string? OriginalStateJson,
    string? DesiredStateJson,
    string? CurrentStateJson,
    SessionCheckpoint? SessionCheckpoint,
    string? Details,
    DateTimeOffset TimestampUtc);

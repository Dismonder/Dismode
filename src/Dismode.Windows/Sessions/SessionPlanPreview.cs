using Dismode.Core.Domain.Identifiers;

namespace Dismode.Windows.Sessions;

public sealed record SessionPlanPreview(
    Guid PlanId,
    SessionId SessionId,
    GameProfileId ProfileId,
    string GameDisplayName,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SessionPlanItem> Items,
    bool SystemMutationsEnabled,
    string SafetyMessage);

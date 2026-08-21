using GameShift.Core.Domain.Identifiers;

namespace GameShift.Windows.Sessions;

public sealed record SessionPlanPreview(
    Guid PlanId,
    SessionId SessionId,
    GameProfileId ProfileId,
    string GameDisplayName,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SessionPlanItem> Items,
    bool SystemMutationsEnabled,
    string SafetyMessage);

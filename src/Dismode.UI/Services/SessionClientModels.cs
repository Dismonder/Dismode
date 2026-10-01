namespace Dismode.UI.Services;

public sealed record SessionPlanActionClientSnapshot(
    string Code,
    string Description,
    string Risk,
    string Recovery);

public sealed record SessionPlanClientSnapshot(
    Guid PlanId,
    Guid SessionId,
    Guid ProfileId,
    string GameDisplayName,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<SessionPlanActionClientSnapshot> Actions,
    bool SystemMutationsEnabled,
    string SafetyMessage);

public sealed record SessionStateClientSnapshot(
    Guid SessionId,
    Guid ProfileId,
    string GameDisplayName,
    string State,
    DateTimeOffset StartedAtUtc,
    int AppliedActionCount,
    int RestoredActionCount,
    int ConflictCount,
    int ErrorCount,
    string Message,
    double? FramesPerSecond,
    double? FrameTimeMilliseconds,
    string FrameRateStatus,
    int? FrameRateProcessId);

public sealed record SessionShutdownReadinessClientSnapshot(
    bool CanShutdown,
    bool HasActiveSession,
    bool HasPreparedPlan,
    string Message);

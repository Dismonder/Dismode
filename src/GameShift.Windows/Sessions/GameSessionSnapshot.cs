using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Sessions;

namespace GameShift.Windows.Sessions;

public sealed record GameSessionSnapshot(
    SessionId SessionId,
    GameProfileId ProfileId,
    string GameDisplayName,
    OptimizationSessionState State,
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

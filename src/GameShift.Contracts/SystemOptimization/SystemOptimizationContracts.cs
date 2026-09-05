namespace GameShift.Contracts.SystemOptimization;

public enum SystemTweakRisk
{
    Safe = 0,
    Experimental = 1,
    Dangerous = 2,
    Blocked = 3,
}

public enum SystemTweakScope
{
    GameSession = 0,
    Global = 1,
}

public enum RestartRequirement
{
    None = 0,
    GameRestart = 1,
    SignOut = 2,
    WindowsRestart = 3,
}

public enum SystemTweakAvailability
{
    Supported = 0,
    Unsupported = 1,
    BlockedByPolicy = 2,
}

public enum BenchmarkVariant
{
    Baseline = 0,
    Candidate = 1,
}

public enum BenchmarkVerdict
{
    Inconclusive = 0,
    CandidateWins = 1,
    KeepBaseline = 2,
    Invalid = 3,
}

public enum ExperimentState
{
    Draft = 0,
    WaitingForScene = 1,
    Stabilizing = 2,
    Capturing = 3,
    WaitingForRestart = 4,
    Evaluating = 5,
    Completed = 6,
    Restoring = 7,
    Failed = 8,
}

public enum RecoveryPhase
{
    Baseline = 0,
    SnapshotCaptured = 1,
    CandidatePendingReboot = 2,
    PostBootVerification = 3,
    Capture = 4,
    RestorePendingReboot = 5,
    VerifiedRestored = 6,
    PromotedGlobally = 7,
    Failed = 8,
    GameSessionActive = 9,
}

public enum RecoveryTimeoutDecision
{
    None = 0,
    Wait = 1,
    Restore = 2,
}

public sealed record TweakDefinition(
    string Id,
    int Revision,
    string DisplayName,
    string Category,
    string Description,
    SystemTweakRisk Risk,
    SystemTweakScope Scope,
    RestartRequirement RestartRequirement,
    SystemTweakAvailability Availability,
    IReadOnlyList<string> AllowedValues,
    IReadOnlyList<string> ConflictsWith,
    string? ExecutionAdapterId,
    string TechnicalSource,
    string RestoreDescription,
    string? BlockingReason,
    int MinimumWindowsBuild = 22631,
    int? MaximumWindowsBuild = null,
    bool RequiresTarget = false);

public sealed record TweakSelection(
    string TweakId,
    int Revision,
    string Value,
    string? TargetId = null,
    string? DangerousConfirmation = null);

public sealed record HardwareFingerprint(
    int SchemaVersion,
    string WindowsBuild,
    string CpuArchitecture,
    string CpuVendor,
    string CpuModelFamily,
    IReadOnlyList<string> GraphicsAdapters,
    IReadOnlyList<string> GraphicsDriverVersions,
    IReadOnlyList<string> NetworkAdapterClasses,
    long PhysicalMemoryBytes,
    bool IsLaptop,
    string FingerprintHash);

public sealed record ExperimentPlan(
    Guid ExperimentId,
    string OwnerSid,
    string GameProfileId,
    string GameExecutableHash,
    string HardwareFingerprintHash,
    TweakSelection Selection,
    BenchmarkVariant FirstVariant,
    ExperimentState State,
    int RequiredPairs,
    int CompletedBaselineCaptures,
    int CompletedCandidateCaptures,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record BenchmarkCapture(
    Guid CaptureId,
    BenchmarkVariant Variant,
    DateTimeOffset StartedAtUtc,
    TimeSpan StabilizationDuration,
    TimeSpan MeasurementDuration,
    IReadOnlyList<double> FrameTimesMilliseconds,
    double? AverageCpuPercent,
    double? AverageGpuBusyPercent,
    long? AverageUsedRamBytes,
    string ProfileStateHash);

public sealed record BenchmarkResult(
    Guid ExperimentId,
    BenchmarkVerdict Verdict,
    double PrimaryMetricImprovementPercent,
    double BaselineAverageFramesPerSecond,
    double CandidateAverageFramesPerSecond,
    double BaselineOnePercentLowFramesPerSecond,
    double CandidateOnePercentLowFramesPerSecond,
    double BaselineHitchRatePercent,
    double CandidateHitchRatePercent,
    int CompletedPairs,
    string Explanation,
    DateTimeOffset EvaluatedAtUtc);

public sealed record PerGameOptimizationProfile(
    Guid ProfileId,
    string OwnerSid,
    string GameProfileId,
    string HardwareFingerprintHash,
    IReadOnlyList<TweakSelection> Selections,
    bool Enabled,
    DateTimeOffset UpdatedAtUtc);

public sealed record ActiveGameOptimizationSession(
    Guid ActivationId,
    string OwnerSid,
    Guid ProfileId,
    string GameProfileId,
    string GameExecutableHash,
    string HardwareFingerprintHash,
    IReadOnlyList<TweakSelection> Selections,
    int AppliedSelectionCount,
    DateTimeOffset ActivatedAtUtc);

public sealed record GlobalOptimizationProfile(
    Guid ProfileId,
    string OwnerSid,
    string HardwareFingerprintHash,
    IReadOnlyList<TweakSelection> Selections,
    bool Enabled,
    DateTimeOffset UpdatedAtUtc,
    Guid? SourceExperimentId = null);

public sealed record RecoveryStatus(
    RecoveryPhase Phase,
    Guid? ExperimentId,
    DateTimeOffset? PhaseEnteredAtUtc,
    bool IsJournalClean,
    bool HasConflicts,
    IReadOnlyList<string> ConflictTargets,
    string Message);

public sealed record SystemOptimizerStatus(
    bool IsServiceReady,
    bool IsReadOnly,
    bool HasUserConsent,
    Guid? ActiveExperimentId,
    Guid? ActiveGlobalProfileId,
    string? ActiveGameProfileId,
    RecoveryStatus Recovery,
    string Message,
    DateTimeOffset ObservedAtUtc,
    ExperimentPlan? ActiveExperiment = null);

public sealed record SystemOptimizationHistoryRecord(
    long Sequence,
    string OwnerSid,
    string OperationKind,
    string? TargetId,
    bool Success,
    string Details,
    DateTimeOffset CreatedAtUtc);

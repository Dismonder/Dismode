namespace GameShift.UI.Services;

public sealed record BackgroundApplicationClientSelection(
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    BackgroundProcessClientActionMode ActionMode);

public enum BackgroundProcessClientActionMode
{
    CloseAndRestore = 1,
    LowerPriority = 2,
    LowerPriorityAndEcoQos = 3,
}

public sealed record DiscoveredProcessClientSnapshot(
    int ProcessId,
    string Name,
    DateTimeOffset? StartedAtUtc,
    string ExecutablePath,
    int SessionId,
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime,
    bool HasMainWindow,
    string SafetyClassification,
    string ClassificationReason,
    string RecommendedAction,
    string PriorityClass);

public sealed record DiscoveredServiceClientSnapshot(
    string ServiceName,
    string DisplayName,
    string Status,
    bool CanStop,
    string ServiceType,
    IReadOnlyList<string> Dependencies,
    string StartType,
    string SafetyClassification,
    string ClassificationReason,
    string RecommendedAction,
    bool IsDriver,
    bool IsSharedProcess,
    int TriggerCount,
    IReadOnlyList<string> RunningDependentServices);

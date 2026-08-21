namespace GameShift.Windows.Services;

public sealed record ServiceSnapshot(
    string ServiceName,
    string DisplayName,
    string Status,
    bool CanStop,
    string ServiceType,
    IReadOnlyList<string> Dependencies,
    string StartType,
    string BinaryPath,
    string ServiceAccount,
    bool IsDriver,
    bool IsSharedProcess,
    int TriggerCount,
    IReadOnlyList<string> RunningDependentServices,
    bool IsConfigurationComplete = true);

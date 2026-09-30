namespace Dismode.Windows.Processes;

public sealed record ProcessSnapshot(
    int ProcessId,
    string Name,
    DateTimeOffset? StartedAtUtc,
    string? ExecutablePath,
    int SessionId,
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime,
    bool HasMainWindow,
    string PriorityClass = "Unknown");

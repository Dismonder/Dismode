using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed record ProcessTelemetrySample(
    ProcessRuntimeKey RuntimeKey,
    DateTimeOffset ObservedAtUtc,
    double CpuPercent,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    int HandleCount);

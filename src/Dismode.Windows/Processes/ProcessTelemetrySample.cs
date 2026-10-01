using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed record ProcessTelemetrySample(
    ProcessRuntimeKey RuntimeKey,
    DateTimeOffset ObservedAtUtc,
    double CpuPercent,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    int ThreadCount,
    int HandleCount);

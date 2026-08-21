using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed record GameProcessTreeObservation(
    IReadOnlyList<ProcessIdentity> KnownProcesses,
    IReadOnlyList<ProcessIdentity> RunningProcesses,
    int NewlyDiscoveredCount,
    bool IsReliable)
{
    public bool HasRunningProcess => RunningProcesses.Count > 0;
}

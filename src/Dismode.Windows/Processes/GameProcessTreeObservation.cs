using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed record GameProcessTreeObservation(
    IReadOnlyList<ProcessIdentity> KnownProcesses,
    IReadOnlyList<ProcessIdentity> RunningProcesses,
    int NewlyDiscoveredCount,
    bool IsReliable)
{
    public bool HasRunningProcess => RunningProcesses.Count > 0;
}

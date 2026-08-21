using System.Diagnostics;

namespace GameShift.Windows.Processes;

public sealed record RuntimeProcessPriorityState(
    bool IsRunning,
    ProcessPriorityClass? PriorityClass);

using System.Diagnostics;

namespace Dismode.Windows.Processes;

public sealed record RuntimeProcessPriorityState(
    bool IsRunning,
    ProcessPriorityClass? PriorityClass);

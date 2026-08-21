namespace GameShift.Windows.Processes;

public sealed record RuntimeProcessEcoQosState(
    bool IsRunning,
    bool? ExecutionSpeedThrottled);

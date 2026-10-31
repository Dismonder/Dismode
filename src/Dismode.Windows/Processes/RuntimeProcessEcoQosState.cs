using System.Text.Json.Serialization;

namespace Dismode.Windows.Processes;

[JsonConverter(typeof(RuntimeProcessEcoQosStateJsonConverter))]
public sealed record RuntimeProcessEcoQosState(
    bool IsRunning,
    EcoQosMode? Mode)
{
    public bool? ExecutionSpeedThrottled =>
        Mode is null ? null : Mode == EcoQosMode.On;
}

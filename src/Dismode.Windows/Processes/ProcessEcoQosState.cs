using System.Text.Json.Serialization;

namespace Dismode.Windows.Processes;

[JsonConverter(typeof(ProcessEcoQosStateJsonConverter))]
public sealed record ProcessEcoQosState(EcoQosMode Mode)
{
    public bool ExecutionSpeedThrottled => Mode == EcoQosMode.On;
}

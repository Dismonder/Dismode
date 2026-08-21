using System.Text.Json.Serialization;

namespace GameShift.Core.Domain.Processes;

public readonly record struct ProcessRuntimeKey
{
    [JsonConstructor]
    public ProcessRuntimeKey(int processId, DateTimeOffset startedAtUtc)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                processId,
                "A process ID must be positive.");
        }

        ProcessId = processId;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
    }

    public int ProcessId { get; }

    public DateTimeOffset StartedAtUtc { get; }
}

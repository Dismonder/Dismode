using System.Text.Json.Serialization;

namespace Dismode.Contracts.SystemOptimization;

public enum BenchmarkCaptureLossState
{
    None = 0,
    Confirmed = 1,
    Unknown = 2,
}

public sealed record BenchmarkCaptureLoss(
    BenchmarkCaptureLossState State,
    long LostEvents = 0,
    long LostBuffers = 0)
{
    public static BenchmarkCaptureLoss None { get; } = new(BenchmarkCaptureLossState.None);

    [JsonIgnore]
    public bool HasConfirmedLoss =>
        State == BenchmarkCaptureLossState.Confirmed || LostEvents > 0 || LostBuffers > 0;
}

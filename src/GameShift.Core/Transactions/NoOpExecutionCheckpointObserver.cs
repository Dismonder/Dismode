namespace GameShift.Core.Transactions;

public sealed class NoOpExecutionCheckpointObserver : IExecutionCheckpointObserver
{
    public static NoOpExecutionCheckpointObserver Instance { get; } = new();

    private NoOpExecutionCheckpointObserver()
    {
    }

    public ValueTask OnCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}


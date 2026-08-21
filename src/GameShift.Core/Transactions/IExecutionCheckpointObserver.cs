namespace GameShift.Core.Transactions;

public interface IExecutionCheckpointObserver
{
    ValueTask OnCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CancellationToken cancellationToken);
}


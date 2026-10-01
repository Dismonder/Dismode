namespace Dismode.Core.Transactions;

public interface IExecutionCheckpointObserver
{
    ValueTask OnCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CancellationToken cancellationToken);
}


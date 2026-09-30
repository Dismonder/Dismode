using Dismode.Core.Transactions;

namespace Dismode.RecoveryTests.Support;

internal sealed class ThrowingCheckpointObserver : IExecutionCheckpointObserver
{
    private readonly ExecutionCheckpoint _checkpoint;
    private bool _hasThrown;

    public ThrowingCheckpointObserver(ExecutionCheckpoint checkpoint)
    {
        _checkpoint = checkpoint;
    }

    public ValueTask OnCheckpointAsync(
        ExecutionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (!_hasThrown && checkpoint == _checkpoint)
        {
            _hasThrown = true;
            throw new InvalidOperationException($"Injected crash at {checkpoint}.");
        }

        return ValueTask.CompletedTask;
    }
}


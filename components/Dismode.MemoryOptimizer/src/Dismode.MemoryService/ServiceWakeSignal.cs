namespace Dismode.MemoryService;

internal sealed class ServiceWakeSignal(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    internal void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Wystarczy jedno oczekujace powiadomienie.
        }
        catch (ObjectDisposedException)
        {
            // Ostatnia zmiana sesji moze dotrzec juz po zatrzymaniu uslugi.
        }
    }

    internal async Task WaitAsync(TimeSpan fallbackInterval, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(fallbackInterval, _clock);
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            await _signal.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
        }
    }

    public void Dispose() => _signal.Dispose();
}

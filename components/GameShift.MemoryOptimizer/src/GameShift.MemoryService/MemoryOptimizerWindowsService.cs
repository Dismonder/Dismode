using System.ServiceProcess;

namespace GameShift.MemoryService;

internal sealed class MemoryOptimizerWindowsService : ServiceBase
{
    private MemoryServiceRuntime? _runtime;

    internal MemoryOptimizerWindowsService()
    {
        ServiceName = "GameShiftMemoryService";
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _runtime = new();
        _runtime.Start();
    }

    protected override void OnStop()
    {
        StopRuntime();
    }

    protected override void OnShutdown()
    {
        StopRuntime();
        base.OnShutdown();
    }

    internal static async Task RunConsoleAsync(
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            lifetime.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using MemoryServiceRuntime runtime = new();
            runtime.Start();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                lifetime.IsCancellationRequested)
            {
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private void StopRuntime()
    {
        MemoryServiceRuntime? runtime = Interlocked.Exchange(
            ref _runtime,
            null);
        if (runtime is null)
        {
            return;
        }

        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

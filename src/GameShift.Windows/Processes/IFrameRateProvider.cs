namespace GameShift.Windows.Processes;

public interface IFrameRateProvider : IAsyncDisposable
{
    ValueTask<FrameRateSample> SampleAsync(
        IReadOnlyCollection<int> processIds,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}

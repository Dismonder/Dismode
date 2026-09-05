namespace GameShift.Windows.Processes;

internal sealed class BenchmarkFrameCollector
{
    internal const int MaximumFrameCount = 120_000;

    private readonly int _targetProcessId;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<PresentMonFrame>> _streams =
        new(StringComparer.Ordinal);
    private int _frameCount;

    internal BenchmarkFrameCollector(int targetProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetProcessId);
        _targetProcessId = targetProcessId;
    }

    internal void Add(PresentMonFrame frame)
    {
        if (frame.ProcessId != _targetProcessId)
        {
            return;
        }

        lock (_sync)
        {
            if (_frameCount >= MaximumFrameCount)
            {
                return;
            }

            if (!_streams.TryGetValue(
                    frame.SwapChainAddress,
                    out List<PresentMonFrame>? stream))
            {
                stream = [];
                _streams.Add(frame.SwapChainAddress, stream);
            }

            stream.Add(frame);
            _frameCount++;
        }
    }

    internal IReadOnlyList<double> SnapshotDominantStream()
        => SnapshotDominantCapture().FrameTimesMilliseconds;

    internal PresentMonBenchmarkCapture SnapshotDominantCapture()
    {
        lock (_sync)
        {
            List<PresentMonFrame>? best = _streams
                .OrderByDescending(pair => pair.Value.Count)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .FirstOrDefault();
            if (best is null)
            {
                return new([], AverageGpuBusyPercent: null);
            }

            PresentMonFrame[] gpuFrames = best
                .Where(frame => frame.GpuBusyMilliseconds.HasValue)
                .ToArray();
            double? averageGpuBusyPercent = gpuFrames.Length == 0
                ? null
                : Math.Clamp(
                    gpuFrames.Sum(frame => frame.GpuBusyMilliseconds!.Value)
                    / gpuFrames.Sum(frame => frame.FrameTimeMilliseconds)
                    * 100d,
                    0d,
                    100d);
            return new(
                best.Select(frame => frame.FrameTimeMilliseconds).ToArray(),
                averageGpuBusyPercent);
        }
    }
}

public sealed record PresentMonBenchmarkCapture(
    IReadOnlyList<double> FrameTimesMilliseconds,
    double? AverageGpuBusyPercent);

using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Presentation;

internal sealed class MemoryRamTrend
{
    private readonly List<MemorySnapshot> _samples = [];

    public IReadOnlyList<MemorySnapshot> Samples => _samples;

    public bool Add(MemorySnapshot snapshot)
    {
        if (snapshot.TotalPhysicalBytes == 0 ||
            snapshot.AvailablePhysicalBytes > snapshot.TotalPhysicalBytes ||
            snapshot.MemoryLoadPercent > 100 ||
            (_samples.Count > 0 &&
             snapshot.CapturedAtUtc - _samples[^1].CapturedAtUtc < TimeSpan.FromSeconds(5)))
        {
            return false;
        }

        _samples.RemoveAll(sample =>
            snapshot.CapturedAtUtc - sample.CapturedAtUtc > TimeSpan.FromMinutes(1));
        _samples.Add(snapshot);
        return true;
    }
}

using Dismode.Windows.Cpu;

namespace Dismode.IntegrationTests.Cpu;

/// <summary>
/// In-memory stand-in for the default CPU sets of one process, with the
/// failures the real API can produce: an unreadable assignment, a refused
/// write, and a read-back that differs from what was written.
/// </summary>
internal sealed class FakeCpuSetsPort : ICpuSetsPort
{
    public uint[] Current { get; set; } = [];

    public bool QueryFails { get; set; }

    public bool ApplyFails { get; set; }

    /// <summary>What a read right after a write shows instead of the write.</summary>
    public uint[]? ReadBackAfterApply { get; set; }

    public int Writes { get; private set; }

    public List<uint[]> Written { get; } = [];

    public CpuSetsQuery Query(int processId) =>
        QueryFails
            ? CpuSetsQuery.Failure
            : new(
                Current.Length == 0
                    ? CpuSetsQueryState.Empty
                    : CpuSetsQueryState.NonEmpty,
                Current);

    public bool Apply(int processId, IReadOnlyList<uint> cpuSetIds)
    {
        Writes++;
        Written.Add([.. cpuSetIds]);
        if (ApplyFails)
        {
            return false;
        }

        Current = ReadBackAfterApply is { } readBack && cpuSetIds.Count > 0
            ? readBack
            : [.. cpuSetIds];
        return true;
    }
}

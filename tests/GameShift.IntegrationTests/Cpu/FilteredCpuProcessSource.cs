using GameShift.Windows.Cpu;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Reports only the processes a test owns to the restraint loop.
/// <para>
/// A test that drives the real actuator against the real sampler restrains
/// whatever is hot on the machine at that moment: the editor, the build
/// server, another session's measurement hogs. The mask and the I/O priority
/// it applies are inherited by every process those start afterwards, and a
/// test that fails before its cleanup leaves them there. Every process a
/// test can restrain must therefore be one it started itself.
/// </para>
/// </summary>
internal sealed class FilteredCpuProcessSource(
    Func<IEnumerable<int>> ownedProcessIds) : ICpuProcessSource
{
    private readonly CpuProcessSampler _sampler = new();

    public IReadOnlyList<CpuProcessSample> Capture()
    {
        HashSet<int> owned = [.. ownedProcessIds()];
        return [.. _sampler.Capture()
            .Where(sample => owned.Contains(sample.ProcessId))];
    }
}

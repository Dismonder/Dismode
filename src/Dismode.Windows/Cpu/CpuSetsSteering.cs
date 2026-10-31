namespace Dismode.Windows.Cpu;

/// <summary>
/// What a process's default CPU sets look like right now: nothing assigned,
/// something assigned, or the question could not be answered. The three are
/// different decisions — an unanswered query is not "nothing assigned".
/// </summary>
internal enum CpuSetsQueryState
{
    Empty,
    NonEmpty,
    Failed,
}

internal readonly record struct CpuSetsQuery(
    CpuSetsQueryState State,
    IReadOnlyList<uint> Ids)
{
    internal static CpuSetsQuery Failure { get; } =
        new(CpuSetsQueryState.Failed, []);
}

/// <summary>
/// The two Win32 operations CPU-sets steering needs, behind a seam so the
/// decision logic can be tested without touching a real process.
/// </summary>
internal interface ICpuSetsPort
{
    CpuSetsQuery Query(int processId);

    bool Apply(int processId, IReadOnlyList<uint> cpuSetIds);
}

internal sealed class SystemCpuSetsPort : ICpuSetsPort
{
    internal static SystemCpuSetsPort Instance { get; } = new();

    private SystemCpuSetsPort()
    {
    }

    public CpuSetsQuery Query(int processId) => ProcessCpuSets.Query(processId);

    public bool Apply(int processId, IReadOnlyList<uint> cpuSetIds) =>
        ProcessCpuSets.TryApply(processId, cpuSetIds);
}

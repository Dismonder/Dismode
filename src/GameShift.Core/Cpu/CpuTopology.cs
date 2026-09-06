namespace GameShift.Core.Cpu;

/// <summary>
/// One logical processor as Windows describes it in a CPU set.
/// <para>
/// <see cref="EfficiencyClass"/> is the number Windows assigns to the core's
/// performance tier: higher is faster. On a uniform machine every core reports
/// the same class. On a hybrid one — Intel's P-core/E-core split, or AMD's
/// mixed CCDs — the classes differ, and that difference is the only reliable
/// way to tell the tiers apart. Core counts, model names and clock speeds are
/// not; they vary by SKU and get misread.
/// </para>
/// </summary>
public sealed record CpuLogicalProcessor(
    uint Id,
    ushort Group,
    byte LogicalProcessorIndex,
    uint CoreIndex,
    byte EfficiencyClass,
    bool Parked,
    bool Allocated);

public sealed record CpuTopology(IReadOnlyList<CpuLogicalProcessor> Processors)
{
    /// <summary>
    /// True when the machine mixes performance tiers, so pinning to the fast
    /// tier is a meaningful choice rather than a no-op.
    /// </summary>
    public bool IsHybrid => Processors
        .Select(processor => processor.EfficiencyClass)
        .Distinct()
        .Count() > 1;

    public byte HighestEfficiencyClass => Processors.Count == 0
        ? (byte)0
        : Processors.Max(processor => processor.EfficiencyClass);

    public byte LowestEfficiencyClass => Processors.Count == 0
        ? (byte)0
        : Processors.Min(processor => processor.EfficiencyClass);

    /// <summary>
    /// Logical processors in the fastest tier — Intel's P-cores on a hybrid
    /// part, and simply every core on a uniform one.
    /// </summary>
    public IReadOnlyList<CpuLogicalProcessor> PerformanceCores =>
        [.. Processors.Where(processor =>
            processor.EfficiencyClass == HighestEfficiencyClass)];

    public IReadOnlyList<CpuLogicalProcessor> EfficiencyCores => IsHybrid
        ? [.. Processors.Where(processor =>
            processor.EfficiencyClass == LowestEfficiencyClass)]
        : [];

    /// <summary>
    /// How many physical cores back a tier. Two logical processors sharing a
    /// core index are one core with SMT, and counting them as two overstates
    /// what pinning actually buys.
    /// </summary>
    public static int CountPhysicalCores(
        IEnumerable<CpuLogicalProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        return processors
            .Select(processor => (processor.Group, processor.CoreIndex))
            .Distinct()
            .Count();
    }
}

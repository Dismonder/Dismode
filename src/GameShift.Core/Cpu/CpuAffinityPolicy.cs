namespace GameShift.Core.Cpu;

public enum CpuAffinityRole
{
    /// <summary>The game, and anything in its process tree.</summary>
    Foreground = 0,

    /// <summary>Everything GameShift is allowed to push out of the way.</summary>
    Background = 1,
}

public enum CpuAffinityDecline
{
    None = 0,

    /// <summary>Every core is the same speed, so pinning moves nothing.</summary>
    UniformTopology = 1,

    /// <summary>Too few fast cores to hold a game without starving it.</summary>
    PerformanceTierTooSmall = 2,

    /// <summary>Too few slow cores to be worth exiling anything to.</summary>
    EfficiencyTierTooSmall = 3,

    /// <summary>
    /// More than 64 logical processors, so affinity spans processor groups.
    /// A single 64-bit mask cannot express that and silently truncating it
    /// would pin the process to the wrong half of the machine.
    /// </summary>
    MultipleProcessorGroups = 4,

    /// <summary>Nothing was read from the system.</summary>
    TopologyUnavailable = 5,
}

public sealed record CpuAffinityDecision(
    bool ShouldApply,
    ulong Mask,
    CpuAffinityDecline Decline,
    string Explanation);

/// <summary>
/// Decides which cores a process should be allowed to run on.
/// <para>
/// Pinning is only ever a win when the machine actually has slower cores to
/// keep work away from. On a uniform CPU it does nothing useful and it can do
/// harm: Windows' scheduler already balances load, and a fixed mask stops it
/// from reacting. So this declines by default and applies only where the
/// hardware gives it something to work with.
/// </para>
/// </summary>
public static class CpuAffinityPolicy
{
    /// <summary>
    /// A game pinned to fewer cores than this loses more to contention than it
    /// gains from cache locality, so below it we leave the scheduler alone.
    /// </summary>
    public const int MinimumPerformancePhysicalCores = 4;

    /// <summary>
    /// Below this there is nowhere to move background work to, and squeezing
    /// it into one or two cores just builds a queue that stalls the game when
    /// it waits on any of it.
    /// </summary>
    public const int MinimumEfficiencyPhysicalCores = 2;

    public static CpuAffinityDecision Decide(
        CpuTopology? topology,
        CpuAffinityRole role)
    {
        if (topology is null || topology.Processors.Count == 0)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.TopologyUnavailable,
                "Nie udało się odczytać topologii procesora.");
        }

        if (topology.Processors.Any(processor => processor.Group != 0))
        {
            return new(
                false,
                0,
                CpuAffinityDecline.MultipleProcessorGroups,
                "Ten komputer ma więcej niż jedną grupę procesorów. "
                    + "GameShift nie zmienia w takim układzie przypisania "
                    + "rdzeni, bo jedna maska nie opisuje całej maszyny.");
        }

        if (!topology.IsHybrid)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.UniformTopology,
                "Wszystkie rdzenie są tej samej klasy wydajności, więc "
                    + "przypinanie do rdzeni niczego by nie zmieniło.");
        }

        IReadOnlyList<CpuLogicalProcessor> performance =
            topology.PerformanceCores;
        IReadOnlyList<CpuLogicalProcessor> efficiency =
            topology.EfficiencyCores;

        int performanceCores = CpuTopology.CountPhysicalCores(performance);
        if (performanceCores < MinimumPerformancePhysicalCores)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.PerformanceTierTooSmall,
                $"Szybkich rdzeni jest tylko {performanceCores}; poniżej "
                    + $"{MinimumPerformancePhysicalCores} przypięcie gry "
                    + "częściej szkodzi, niż pomaga.");
        }

        int efficiencyCores = CpuTopology.CountPhysicalCores(efficiency);
        if (efficiencyCores < MinimumEfficiencyPhysicalCores)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.EfficiencyTierTooSmall,
                $"Wolnych rdzeni jest tylko {efficiencyCores}; nie ma dokąd "
                    + "przenieść procesów tła.");
        }

        IReadOnlyList<CpuLogicalProcessor> target = role switch
        {
            CpuAffinityRole.Foreground => performance,
            CpuAffinityRole.Background => efficiency,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

        ulong mask = BuildMask(target);
        return mask == 0
            ? new(
                false,
                0,
                CpuAffinityDecline.TopologyUnavailable,
                "Wyliczona maska rdzeni jest pusta.")
            : new(
                true,
                mask,
                CpuAffinityDecline.None,
                role == CpuAffinityRole.Foreground
                    ? $"Gra dostanie {performanceCores} szybkich rdzeni "
                        + $"({target.Count} procesorów logicznych)."
                    : $"Procesy tła trafią na {efficiencyCores} wolnych "
                        + $"rdzeni ({target.Count} procesorów logicznych).");
    }

    /// <summary>
    /// Turns logical processor ids into the bitmask Windows expects. Ids at or
    /// above 64 cannot appear in a single-group mask; the caller has already
    /// declined that case, so reaching one here is a bug worth failing on
    /// rather than quietly dropping a core.
    /// </summary>
    public static ulong BuildMask(IEnumerable<CpuLogicalProcessor> processors)
    {
        ArgumentNullException.ThrowIfNull(processors);
        ulong mask = 0;
        foreach (CpuLogicalProcessor processor in processors)
        {
            byte index = processor.LogicalProcessorIndex;
            if (index >= 64)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(processors),
                    index,
                    "Indeks procesora logicznego nie mieści się w masce.");
            }

            mask |= 1UL << index;
        }

        return mask;
    }
}

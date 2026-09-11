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

    /// <summary>
    /// Every core is the same speed and they all share one last-level cache,
    /// so there is no boundary worth keeping the game on one side of.
    /// </summary>
    UniformTopology = 1,

    /// <summary>Too few fast cores to hold a game without starving it.</summary>
    PerformanceTierTooSmall = 2,

    /// <summary>Too few slow cores to be worth exiling anything to.</summary>
    EfficiencyTierTooSmall = 3,

    /// <summary>
    /// The machine has separate cache groups but the largest one is too small
    /// to hold a game without starving it.
    /// </summary>
    CacheGroupTooSmall = 6,

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
    /// <summary>
    /// Below this a quarter of the machine is too coarse a cut: the
    /// background work has nowhere to live and the shell starts to
    /// stutter, trading one kind of jank for another.
    /// </summary>
    public const int MinimumLogicalProcessorsForCorner = 8;

    public const int MinimumPerformancePhysicalCores = 4;

    /// <summary>
    /// Below this there is nowhere to move background work to, and squeezing
    /// it into one or two cores just builds a queue that stalls the game when
    /// it waits on any of it.
    /// </summary>
    public const int MinimumEfficiencyPhysicalCores = 2;

    /// <summary>
    /// Corner of a uniform machine that background work is confined to: the
    /// lowest quarter of the logical processors, never fewer than two.
    /// <para>
    /// The share is the one that was measured, not a guess — four of sixteen
    /// threads. Below <see cref="MinimumLogicalProcessorsForCorner"/> a quarter
    /// is too coarse a cut: the background has nowhere to live and the shell
    /// starts to stutter, which trades one kind of jank for another.
    /// </para>
    /// </summary>
    private static CpuAffinityDecision DecideBackgroundCorner(
        CpuTopology topology)
    {
        List<CpuLogicalProcessor> usable = [.. topology.Processors
            .Where(processor => processor.LogicalProcessorIndex < 64)
            .OrderBy(processor => processor.LogicalProcessorIndex)];
        if (usable.Count < MinimumLogicalProcessorsForCorner)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.UniformTopology,
                $"Ta maszyna ma {usable.Count} procesorów logicznych. "
                    + "Odebranie ćwiartki z tak małej puli częściej szkodzi, "
                    + "niż pomaga.");
        }

        // Cwiartka puli, ale liczona calymi rdzeniami fizycznymi. Wersja
        // biorąca po prostu N najnizszych procesorow logicznych dzielila
        // rdzen na pol wszedzie tam, gdzie cwiartka wypadala nieparzysto:
        // na 12 watkach (6 rdzeni, czyli i5-12400 albo Ryzen 5 5600) dawala
        // logiczne 0,1,2 — caly rdzen 0 i polowe rdzenia 1. Drugi watek
        // rdzenia 1 zostawal grze, wiec gra i tlo lądowaly na tym samym
        // rdzeniu fizycznym i bily sie o jego jednostki wykonawcze. To
        // dokladnie ta rywalizacja, ktora ta maska ma likwidowac.
        int target = Math.Max(2, usable.Count / 4);
        List<uint> cores = [.. usable
            .GroupBy(processor => processor.CoreIndex)
            .OrderBy(group => group.Min(
                processor => processor.LogicalProcessorIndex))
            .Select(group => group.Key)];
        HashSet<uint> chosen = [];
        int taken = 0;
        foreach (uint core in cores)
        {
            if (taken >= target)
            {
                break;
            }

            // Rdzen bierzemy w calosci albo wcale, wiec przy nieparzystej
            // cwiartce zaokraglamy w gore. W dol znaczyloby oddac tlu mniej,
            // niz przewiduje polityka, a tlo tez musi gdzies policzyc swoje.
            chosen.Add(core);
            taken += usable.Count(
                processor => processor.CoreIndex == core);
        }

        ulong mask = 0;
        foreach (CpuLogicalProcessor processor in usable.Where(
            processor => chosen.Contains(processor.CoreIndex)))
        {
            mask |= 1UL << processor.LogicalProcessorIndex;
        }

        return new(
            true,
            mask,
            CpuAffinityDecline.None,
            $"Procesy w tle dostają {taken} z {usable.Count} procesorów "
                + $"logicznych ({chosen.Count} pełnych rdzeni). Resztę "
                + "maszyny zostawiamy grze.");
    }

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
            // Rdzenie tej samej klasy nadal moga byc podzielone cache'em
            // ostatniego poziomu — tak wyglada Ryzen z kilkoma CCD. Watki
            // rozrzucone po obu stronach tej granicy rozmawiaja przez pamiec
            // zamiast przez wspolny cache, a klasa wydajnosci nic o tym nie
            // mowi.
            if (topology.HasSeparateCacheGroups)
            {
                return DecideByCacheGroup(topology, role);
            }

            // Dla gry to koniec: gdy rdzenie sa identyczne i dziela cache,
            // przypiecie jej gdziekolwiek niczego nie zmienia. Dla tla jest
            // odwrotnie i dlugo mialem to zle. Chodzi nie o lokalnosc cache'u,
            // tylko o odebranie rdzeni: proces liczacy bez przerwy oddaje je
            // grze dopiero wtedy, gdy nie wolno mu ich dotknac.
            //
            // Zmierzone 2026-09-10 na i7-11700F (8 rdzeni, 16 watkow,
            // jednorodny) w 7 Days To Die, p99 czasu klatki przy tylu petlach
            // liczacych, ile watkow ma maszyna: 16,80 ms gdy tlo bylo wolne,
            // 16,36 ms po samym obnizeniu priorytetu — czyli nic — i 10,65 ms
            // po zamknieciu tla w cwiartce maszyny. Maska wygrala z priorytetem
            // w kazdej z czterech rund, i powtorzylo sie to przy innych
            // ustawieniach graficznych: 12,31 / 12,23 / 9,06 ms.
            return role == CpuAffinityRole.Background
                ? DecideBackgroundCorner(topology)
                : new(
                    false,
                    0,
                    CpuAffinityDecline.UniformTopology,
                    "Wszystkie rdzenie są tej samej klasy wydajności i dzielą "
                        + "wspólny cache, więc przypinanie gry niczego by nie "
                        + "zmieniło.");
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
    /// Keeps the game inside one cache group and pushes background work to the
    /// rest. On a machine whose cores are all the same speed this is the only
    /// boundary that means anything, and on a multi-CCD part it is a sharper
    /// one than the performance tiers ever are.
    /// </summary>
    private static CpuAffinityDecision DecideByCacheGroup(
        CpuTopology topology,
        CpuAffinityRole role)
    {
        IReadOnlyList<CpuLogicalProcessor> largest = topology.LargestCacheGroup;
        int cores = CpuTopology.CountPhysicalCores(largest);
        if (cores < MinimumPerformancePhysicalCores)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.CacheGroupTooSmall,
                $"Największa grupa cache ma tylko {cores} rdzeni; poniżej "
                    + $"{MinimumPerformancePhysicalCores} przypięcie gry "
                    + "częściej szkodzi, niż pomaga.");
        }

        IReadOnlyList<CpuLogicalProcessor> rest =
            [.. topology.Processors.Except(largest)];
        if (CpuTopology.CountPhysicalCores(rest)
            < MinimumEfficiencyPhysicalCores)
        {
            return new(
                false,
                0,
                CpuAffinityDecline.CacheGroupTooSmall,
                "Poza największą grupą cache zostaje za mało rdzeni, żeby "
                    + "przenieść tam procesy tła.");
        }

        IReadOnlyList<CpuLogicalProcessor> target =
            role == CpuAffinityRole.Foreground ? largest : rest;
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
                    ? $"Gra zostanie w jednej grupie cache: {cores} rdzeni "
                        + $"({target.Count} procesorów logicznych)."
                    : "Procesy tła trafią poza grupę cache gry "
                        + $"({target.Count} procesorów logicznych).");
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

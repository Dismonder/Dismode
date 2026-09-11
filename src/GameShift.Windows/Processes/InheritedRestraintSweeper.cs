using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

/// <summary>What one sweep did, so a caller can count failures as errors.</summary>
public sealed record InheritedRestraintSweep(
    int Released,
    int Failed,
    bool SnapshotFailed = false)
{
    public static InheritedRestraintSweep None { get; } = new(0, 0);

    /// <summary>
    /// True when every inherited restraint the sweep could see is gone. A
    /// sweep that could not see the process tree at all is not complete: it
    /// may have left children behind without knowing.
    /// </summary>
    public bool Complete => Failed == 0 && !SnapshotFailed;

    public InheritedRestraintSweep Add(InheritedRestraintSweep other) =>
        new(
            Released + other.Released,
            Failed + other.Failed,
            SnapshotFailed || other.SnapshotFailed);
}

/// <summary>
/// Gives back the machine to processes that inherited a restraint from a
/// restrained parent.
/// <para>
/// An affinity mask, an I/O priority, a memory priority and a BelowNormal
/// priority class are inherited by every process created after they were
/// set — measured on this machine, not assumed — and browsers, launchers and
/// chat clients create processes for the whole length of a game. The journal
/// reverses what was done to the parent and knows nothing about children
/// born confined. Left alone they keep a quarter of the machine and an I/O
/// priority no standard tool even shows, until they exit; for a browser's
/// renderer that can be the rest of the day.
/// </para>
/// <para>
/// Rules that keep this from touching anything it should not:
/// a descendant of the restrained root must have started after the restraint
/// and carry, lever by lever, exactly the value the restraint imposed; an
/// orphan — a process whose recorded parent is gone, the only way to reach a
/// grandchild behind an exited middle generation — must additionally carry
/// the whole signature at once, mask included, because a lone I/O priority
/// is a value some processes pick for themselves. The values written back
/// are what the parent has now, which after restoration is what it had
/// before, or the machine-wide defaults when the parent is gone; the parent
/// is trusted only if its start time still matches, so a recycled id cannot
/// lend its mask. Children are never narrowed: a parent mask that does not
/// contain the corner is replaced by the whole machine. The sweep repeats
/// until a pass releases nothing, because a child restored in one pass may
/// have created a grandchild while the pass ran.
/// </para>
/// </summary>
public static class InheritedRestraintSweeper
{
    /// <summary>
    /// Passes over the tree. Children born while a pass ran inherit from
    /// parents that pass restored, so one snapshot is never the last word;
    /// three passes bound the work while catching the realistic case.
    /// </summary>
    private const int MaximumPasses = 3;

    /// <summary>
    /// Releases descendants of <paramref name="rootProcessId"/>.
    /// </summary>
    /// <param name="rootProcessId">The process whose restraint was reversed.</param>
    /// <param name="rootStartedAtUtc">
    /// When the root started, so a recycled id is not mistaken for it. Null
    /// when unknown, in which case the id is trusted.
    /// </param>
    /// <param name="captureParents">
    /// Takes a fresh child-to-parent snapshot; returns null when Windows
    /// refuses one, which the sweep reports rather than treats as success.
    /// </param>
    /// <param name="notBeforeUtc">Start time before which nothing can have inherited.</param>
    /// <param name="cornerMask">The mask the restraint imposed, or zero when none was.</param>
    /// <param name="resetIoPriority">Whether the restraint lowered I/O priority.</param>
    /// <param name="resetMemoryPriority">Whether the restraint lowered memory priority.</param>
    /// <param name="resetPriorityClass">Whether the restraint lowered the priority class.</param>
    public static InheritedRestraintSweep Release(
        int rootProcessId,
        DateTimeOffset? rootStartedAtUtc,
        Func<IReadOnlyDictionary<int, int>?> captureParents,
        DateTimeOffset notBeforeUtc,
        ulong cornerMask,
        bool resetIoPriority,
        bool resetMemoryPriority,
        bool resetPriorityClass)
    {
        ArgumentNullException.ThrowIfNull(captureParents);
        if (cornerMask == 0
            && !resetIoPriority
            && !resetMemoryPriority
            && !resetPriorityClass)
        {
            return InheritedRestraintSweep.None;
        }

        RestoreTargets targets = ReadRestoreTargets(
            rootProcessId,
            rootStartedAtUtc,
            cornerMask);
        if (targets.Mask == 0
            && !resetIoPriority
            && !resetMemoryPriority
            && !resetPriorityClass)
        {
            return InheritedRestraintSweep.None;
        }

        int released = 0;
        HashSet<int> failed = [];
        for (int pass = 0; pass < MaximumPasses; pass++)
        {
            IReadOnlyDictionary<int, int>? parentMap = captureParents();
            if (parentMap is null)
            {
                return new(released, failed.Count, SnapshotFailed: true);
            }

            int releasedThisPass = 0;
            foreach ((int candidate, bool isOrphan) in SelectCandidates(
                rootProcessId,
                parentMap,
                allowOrphans: cornerMask != 0))
            {
                switch (TryRelease(
                    candidate,
                    isOrphan || !targets.RootTrusted,
                    notBeforeUtc,
                    cornerMask,
                    targets,
                    resetIoPriority,
                    resetMemoryPriority,
                    resetPriorityClass))
                {
                    case ReleaseOutcome.Released:
                        releasedThisPass++;
                        break;
                    case ReleaseOutcome.Failed:
                        failed.Add(candidate);
                        break;
                }
            }

            released += releasedThisPass;
            if (releasedThisPass == 0)
            {
                break;
            }
        }

        return new(released, failed.Count);
    }

    /// <summary>
    /// Descendants of the root by breadth-first walk, then — when allowed —
    /// orphans: processes whose recorded parent no longer exists. The orphan
    /// rule is what reaches a grandchild after its parent exited; the caller
    /// allows it only when the restraint included the mask, so the signature
    /// checked in <see cref="TryRelease"/> is strong enough to stand alone.
    /// Exposed for tests: the walk is pure, the rest needs live processes.
    /// </summary>
    internal static IReadOnlyList<(int ProcessId, bool IsOrphan)> SelectCandidates(
        int rootProcessId,
        IReadOnlyDictionary<int, int> parentMap,
        bool allowOrphans)
    {
        ArgumentNullException.ThrowIfNull(parentMap);
        List<(int, bool)> candidates = [];
        HashSet<int> seen = [rootProcessId, Environment.ProcessId, 0, 4];
        Queue<int> pending = new();
        pending.Enqueue(rootProcessId);
        while (pending.Count > 0)
        {
            int parent = pending.Dequeue();
            foreach ((int child, int childParent) in parentMap)
            {
                // Odwiedzeni chronia przed cyklem, ktory mapa rodzicow potrafi
                // zawierac po ponownym uzyciu identyfikatorow.
                if (childParent == parent && seen.Add(child))
                {
                    pending.Enqueue(child);
                    candidates.Add((child, false));
                }
            }
        }

        if (allowOrphans)
        {
            foreach ((int child, int childParent) in parentMap)
            {
                if (!parentMap.ContainsKey(childParent) && seen.Add(child))
                {
                    candidates.Add((child, true));
                }
            }
        }

        return candidates;
    }

    private enum ReleaseOutcome
    {
        Untouched,
        Released,
        Failed,
    }

    /// <summary>
    /// What a released child gets: the parent's values after restoration, or
    /// the machine-wide defaults when the parent is gone or still carries the
    /// restraint. <see cref="Mask"/> is zero when masks must be left alone.
    /// <see cref="PriorityClass"/> is null when the parent is still
    /// BelowNormal — a BelowNormal child could then have inherited it
    /// legitimately, and the two cases cannot be told apart.
    /// </summary>
    private sealed record RestoreTargets(
        ulong Mask,
        uint IoPriority,
        uint MemoryPriority,
        ProcessPriorityClass? PriorityClass,
        bool RootTrusted);

    private static RestoreTargets ReadRestoreTargets(
        int rootProcessId,
        DateTimeOffset? rootStartedAtUtc,
        ulong cornerMask)
    {
        ulong machine = MachineMask();
        try
        {
            using Process parent = Process.GetProcessById(rootProcessId);
            if (rootStartedAtUtc is DateTimeOffset expectedStart)
            {
                DateTimeOffset actualStart = new(
                    parent.StartTime.ToUniversalTime(),
                    TimeSpan.Zero);
                if (Math.Abs((actualStart - expectedStart).TotalSeconds) > 1)
                {
                    // Numer nalezy juz do kogos innego. Jego maska niczego
                    // nie mowi o tym, co mialy odziedziczyc dzieci — a jego
                    // dzieci w mapie nie sa naszymi potomkami, wiec kazdy
                    // kandydat musi niesc pelny odcisk, jak sierota.
                    return Defaults(machine, cornerMask, rootTrusted: false);
                }
            }

            ulong parentMask = (ulong)parent.ProcessorAffinity.ToInt64();
            ulong mask;
            if (cornerMask == 0)
            {
                mask = 0;
            }
            else if (parentMask == cornerMask)
            {
                // Rodzic nadal siedzi w cwiartce: przywracanie sie nie
                // powiodlo albo ktos tak chcial. Maski dzieci zostaja, jak
                // sa; reszte pakietu i tak oddajemy.
                mask = 0;
            }
            else if ((parentMask & cornerMask) != cornerMask)
            {
                // Maska rodzica nie zawiera cwiartki, wiec przepisanie jej
                // dziecku byloby zawezeniem, nie poszerzeniem. Cala maszyna
                // jest jedyna wartoscia, ktorej nie da sie zarzucic zawezenia.
                mask = machine;
            }
            else
            {
                mask = parentMask;
            }

            uint? parentIo = ReadIoPriority(parent);
            uint io = parentIo is { } value
                && value != IoPriorityNativeMethods.IoPriorityVeryLow
                    ? value
                    : IoPriorityNativeMethods.IoPriorityNormal;
            uint parentMemory =
                ProcessMemoryPriorityAction.Read(parent).MemoryPriority;
            uint memory = parentMemory != ProcessNativeMethods.MemoryPriorityVeryLow
                ? parentMemory
                : ProcessNativeMethods.MemoryPriorityNormal;
            ProcessPriorityClass? priorityClass = parent.PriorityClass
                is ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle
                    ? null
                    : ProcessPriorityClass.Normal;
            return new(mask, io, memory, priorityClass, RootTrusted: true);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return Defaults(machine, cornerMask, rootTrusted: false);
        }
    }

    /// <summary>
    /// Targets for a child whose parent is gone or cannot be trusted: every
    /// processor, normal I/O and memory priority — the only honest defaults
    /// for a process nobody restricted. The priority class stays untouched:
    /// a BelowNormal child of a parent that was itself BelowNormal before we
    /// ever saw it inherited that legitimately, and with the parent gone the
    /// two cases cannot be told apart.
    /// </summary>
    private static RestoreTargets Defaults(
        ulong machine,
        ulong cornerMask,
        bool rootTrusted) =>
        new(
            cornerMask == 0 ? 0 : machine,
            IoPriorityNativeMethods.IoPriorityNormal,
            ProcessNativeMethods.MemoryPriorityNormal,
            PriorityClass: null,
            rootTrusted);

    private static ReleaseOutcome TryRelease(
        int processId,
        bool isOrphan,
        DateTimeOffset notBeforeUtc,
        ulong cornerMask,
        RestoreTargets targets,
        bool resetIoPriority,
        bool resetMemoryPriority,
        bool resetPriorityClass)
    {
        Process? process = null;
        try
        {
            // Faza odczytu. Proces, ktorego nie da sie obejrzec — cudzy,
            // chroniony systemowo, wlasnie znikajacy — nie jest bledem
            // odtwarzania: nie ma dowodu, ze cokolwiek po nas nosi, wiec
            // zostaje nietkniety. Liczenie go jako bledu trzymaloby sesje
            // w stanie „wymaga odtwarzania" z powodu procesu, ktorego nigdy
            // nie ruszylismy.
            bool maskMatches;
            bool ioMatches;
            bool memoryMatches;
            bool classMatches;
            try
            {
                process = Process.GetProcessById(processId);
                if (BackgroundApplicationGuard.IsProtectedProcessName(
                        process.ProcessName))
                {
                    return ReleaseOutcome.Untouched;
                }

                // Proces starszy niz ograniczenie nie mogl go odziedziczyc;
                // cokolwiek ma, ma z wlasnej woli.
                DateTimeOffset startedAtUtc = new(
                    process.StartTime.ToUniversalTime(),
                    TimeSpan.Zero);
                if (startedAtUtc < notBeforeUtc)
                {
                    return ReleaseOutcome.Untouched;
                }

                maskMatches = cornerMask != 0
                    && (ulong)process.ProcessorAffinity.ToInt64() == cornerMask;
                ioMatches = resetIoPriority
                    && ReadIoPriority(process)
                        == IoPriorityNativeMethods.IoPriorityVeryLow;
                memoryMatches = resetMemoryPriority
                    && ProcessMemoryPriorityAction.Read(process).MemoryPriority
                        == ProcessNativeMethods.MemoryPriorityVeryLow;
                classMatches = resetPriorityClass
                    && targets.PriorityClass is not null
                    && process.PriorityClass == ProcessPriorityClass.BelowNormal;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or InvalidOperationException
                    or Win32Exception)
            {
                return ReleaseOutcome.Untouched;
            }

            if (isOrphan)
            {
                // Sierota nie ma udowodnionego pokrewienstwa, wiec musi niesc
                // caly odcisk ograniczenia naraz. Sama maska, sam priorytet
                // I/O albo sama klasa to wartosci, ktore procesy wybieraja
                // tez same; wszystkie naraz — nie.
                bool fullSignature = maskMatches
                    && (!resetIoPriority || ioMatches)
                    && (!resetMemoryPriority || memoryMatches);
                if (!fullSignature)
                {
                    return ReleaseOutcome.Untouched;
                }

                // Klasy priorytetu sierocie nie ruszamy: to najslabszy
                // element odcisku i najlatwiejszy do pomylenia z wyborem.
                classMatches = false;
            }

            // Faza zapisu. Tu niepowodzenie na zywym procesie jest bledem:
            // proces nosi nasze ograniczenie i nie dal go sobie zdjac.
            try
            {
                bool touched = false;
                if (maskMatches && targets.Mask != 0)
                {
                    process!.ProcessorAffinity = (nint)(long)targets.Mask;
                    touched = true;
                }

                if (ioMatches)
                {
                    WriteIoPriority(process!, targets.IoPriority);
                    touched = true;
                }

                if (memoryMatches)
                {
                    ProcessMemoryPriorityAction.Write(
                        process!,
                        targets.MemoryPriority);
                    touched = true;
                }

                if (classMatches && targets.PriorityClass is { } priorityClass)
                {
                    process!.PriorityClass = priorityClass;
                    touched = true;
                }

                return touched
                    ? ReleaseOutcome.Released
                    : ReleaseOutcome.Untouched;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception)
            {
                // Proces, ktory wyszedl miedzy odczytem a zapisem, nie jest
                // bledem odtwarzania: nie ma juz na nim niczego naszego.
                // Zywy proces, ktory nie dal sie zmienic, jest — zostaje
                // z odziedziczonym ograniczeniem.
                return HasExited(process)
                    ? ReleaseOutcome.Untouched
                    : ReleaseOutcome.Failed;
            }
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool HasExited(Process? process)
    {
        try
        {
            return process is null || process.HasExited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static uint? ReadIoPriority(Process process) =>
        IoPriorityNativeMethods.NtQueryInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            out uint priority,
            sizeof(uint),
            out _) == IoPriorityNativeMethods.StatusSuccess
            ? priority
            : null;

    private static void WriteIoPriority(Process process, uint priority)
    {
        int status = IoPriorityNativeMethods.NtSetInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            in priority,
            sizeof(uint));
        if (status != IoPriorityNativeMethods.StatusSuccess)
        {
            throw new InvalidOperationException(
                "Nie udało się przywrócić priorytetu wejścia-wyjścia "
                    + $"potomkowi (NTSTATUS 0x{status:X8}).");
        }
    }

    /// <summary>Every logical processor of the first group, as a mask.</summary>
    public static ulong MachineMask()
    {
        CpuTopology? topology = SystemCpuTopologyProvider.Read();
        return topology is null
            ? 0
            : CpuAffinityPolicy.BuildMask(topology.Processors.Where(
                processor => processor.LogicalProcessorIndex < 64));
    }
}

using System.ComponentModel;
using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

/// <summary>What one sweep did, so a caller can count failures as errors.</summary>
public sealed record InheritedRestraintSweep(int Released, int Failed)
{
    public static InheritedRestraintSweep None { get; } = new(0, 0);

    public InheritedRestraintSweep Add(InheritedRestraintSweep other) =>
        new(Released + other.Released, Failed + other.Failed);
}

/// <summary>
/// Gives back the machine to processes that inherited a restraint from a
/// restrained parent.
/// <para>
/// An affinity mask and an I/O priority are inherited by every process
/// created after they were set — measured on this machine, not assumed —
/// and browsers, launchers and chat clients create processes for the whole
/// length of a game. The journal reverses what was done to the parent and
/// knows nothing about children born confined. Left alone they keep a quarter
/// of the machine and an I/O priority no standard tool even shows, until they
/// exit; for a browser's renderer that can be the rest of the day.
/// </para>
/// <para>
/// Three rules keep this from touching anything it should not. The candidate
/// must descend from the restrained root, or be an orphan whose parent is
/// gone — a grandchild whose middle generation exited would otherwise be
/// unreachable. It must have started after the restraint was applied;
/// nothing older can have inherited it. And it must carry exactly the value
/// the restraint imposed: precisely the corner mask, precisely the lowest
/// I/O or memory priority. Every widening goes to what the parent has now,
/// which after restoration is what it had before, or to the whole machine
/// when the parent is gone. The direction of any residual mistake is safe:
/// a process regains processors it was entitled to.
/// </para>
/// </summary>
public static class InheritedRestraintSweeper
{
    /// <summary>
    /// Releases descendants of <paramref name="rootProcessId"/>.
    /// </summary>
    /// <param name="rootProcessId">The process whose restraint was reversed.</param>
    /// <param name="parentMap">Child to parent, captured after the parent was restored.</param>
    /// <param name="notBeforeUtc">Start time before which nothing can have inherited.</param>
    /// <param name="cornerMask">The mask the restraint imposed, or zero when none was.</param>
    /// <param name="resetIoPriority">Whether the restraint lowered I/O priority.</param>
    /// <param name="resetMemoryPriority">Whether the restraint lowered memory priority.</param>
    public static InheritedRestraintSweep Release(
        int rootProcessId,
        IReadOnlyDictionary<int, int> parentMap,
        DateTimeOffset notBeforeUtc,
        ulong cornerMask,
        bool resetIoPriority,
        bool resetMemoryPriority)
    {
        ArgumentNullException.ThrowIfNull(parentMap);
        if (cornerMask == 0 && !resetIoPriority && !resetMemoryPriority)
        {
            return InheritedRestraintSweep.None;
        }

        ulong restoredMask = cornerMask == 0
            ? 0
            : ReadAffinityOrMachineMask(rootProcessId);
        if (cornerMask != 0 && restoredMask == cornerMask)
        {
            // Rodzic nadal siedzi w cwiartce: przywracanie sie nie powiodlo
            // albo ktos tak chcial. Dzieci zostaja, jak sa — poszerzanie ich
            // do cwiartki nic by nie zmienilo, a do czegos wiecej byloby
            // zgadywaniem.
            return InheritedRestraintSweep.None;
        }

        int released = 0;
        int failed = 0;
        foreach (int candidate in Candidates(rootProcessId, parentMap))
        {
            switch (TryRelease(
                candidate,
                notBeforeUtc,
                cornerMask,
                restoredMask,
                resetIoPriority,
                resetMemoryPriority))
            {
                case ReleaseOutcome.Released:
                    released++;
                    break;
                case ReleaseOutcome.Failed:
                    failed++;
                    break;
            }
        }

        return new(released, failed);
    }

    /// <summary>
    /// Descendants of the root by breadth-first walk, then orphans — processes
    /// whose recorded parent no longer exists. The orphan rule is what reaches
    /// a grandchild after its parent exited: the map still says the grandchild
    /// belongs to a process that is gone, and nothing links that process to
    /// the root any more. The value check in <see cref="TryRelease"/> is what
    /// keeps orphans from being touched indiscriminately.
    /// </summary>
    private static IEnumerable<int> Candidates(
        int rootProcessId,
        IReadOnlyDictionary<int, int> parentMap)
    {
        HashSet<int> seen = [rootProcessId];
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
                    yield return child;
                }
            }
        }

        foreach ((int child, int childParent) in parentMap)
        {
            if (!parentMap.ContainsKey(childParent) && seen.Add(child))
            {
                yield return child;
            }
        }
    }

    private enum ReleaseOutcome
    {
        Untouched,
        Released,
        Failed,
    }

    private static ReleaseOutcome TryRelease(
        int processId,
        DateTimeOffset notBeforeUtc,
        ulong cornerMask,
        ulong restoredMask,
        bool resetIoPriority,
        bool resetMemoryPriority)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
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

            bool touched = false;
            if (cornerMask != 0
                && restoredMask != 0
                && (ulong)process.ProcessorAffinity.ToInt64() == cornerMask)
            {
                process.ProcessorAffinity = (nint)(long)restoredMask;
                touched = true;
            }

            if (resetIoPriority && ReadIoPriority(process)
                == IoPriorityNativeMethods.IoPriorityVeryLow)
            {
                WriteIoPriority(process, IoPriorityNativeMethods.IoPriorityNormal);
                touched = true;
            }

            if (resetMemoryPriority
                && ProcessMemoryPriorityAction.Read(process).MemoryPriority
                    == ProcessNativeMethods.MemoryPriorityVeryLow)
            {
                ProcessMemoryPriorityAction.Write(
                    process,
                    ProcessNativeMethods.MemoryPriorityNormal);
                touched = true;
            }

            return touched ? ReleaseOutcome.Released : ReleaseOutcome.Untouched;
        }
        catch (ArgumentException)
        {
            // Zakonczyl sie, zanim do niego doszlismy. Nie ma czego oddawac.
            return ReleaseOutcome.Untouched;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            // Zyje, ale nie dal sie zmienic. To jest blad odtwarzania, nie
            // szum: proces zostaje z odziedziczonym ograniczeniem.
            return ReleaseOutcome.Failed;
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

    /// <summary>
    /// The mask the restrained parent has now, or the whole machine when it is
    /// already gone — its children can outlive it, and the only honest default
    /// for a process nobody restricted is every processor.
    /// </summary>
    public static ulong ReadAffinityOrMachineMask(int processId)
    {
        try
        {
            using Process parent = Process.GetProcessById(processId);
            return (ulong)parent.ProcessorAffinity.ToInt64();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return MachineMask();
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

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

internal enum CpuSetsSteerOutcome
{
    /// <summary>Written and confirmed by a read-back.</summary>
    Applied,

    /// <summary>The process already had exactly the wanted sets.</summary>
    NoChange,

    /// <summary>Someone else's assignment is there; it was not overwritten.</summary>
    SkippedForeign,

    /// <summary>The current assignment could not be read; nothing was written.</summary>
    SkippedQueryFailed,

    /// <summary>The write failed or the read-back did not match.</summary>
    NotApplied,
}

/// <summary>
/// Result of a steering attempt. <see cref="Original"/> is what the process
/// had before and <see cref="Applied"/> what was written; both are only
/// meaningful for <see cref="CpuSetsSteerOutcome.Applied"/>.
/// </summary>
internal sealed record CpuSetsSteer(
    CpuSetsSteerOutcome Outcome,
    IReadOnlyList<uint> Original,
    IReadOnlyList<uint> Applied)
{
    internal bool IsApplied => Outcome == CpuSetsSteerOutcome.Applied;

    internal static CpuSetsSteer Of(CpuSetsSteerOutcome outcome) =>
        new(outcome, [], []);
}

internal enum CpuSetsRestoreResult
{
    /// <summary>The original assignment is back.</summary>
    Restored,

    /// <summary>
    /// Someone changed the assignment after us, so it is no longer ours to
    /// undo; it was left as it is.
    /// </summary>
    LeftAlone,

    /// <summary>The assignment could not be read or written; worth a retry.</summary>
    Failed,
}

/// <summary>
/// Soft CPU-sets steering that does not trample what is already there:
/// reads the current assignment first, writes only onto an empty one,
/// confirms the write by reading it back, and puts the original back only
/// while the assignment is still the one we wrote.
/// </summary>
internal static class CpuSetsSteering
{
    internal static CpuSetsSteer Steer(
        ICpuSetsPort port,
        int processId,
        IReadOnlyList<uint> target)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Count == 0)
        {
            return CpuSetsSteer.Of(CpuSetsSteerOutcome.NotApplied);
        }

        CpuSetsQuery before = port.Query(processId);
        switch (before.State)
        {
            case CpuSetsQueryState.Failed:
                return CpuSetsSteer.Of(CpuSetsSteerOutcome.SkippedQueryFailed);
            case CpuSetsQueryState.NonEmpty:
                // Launcher albo uzytkownik: nie nadpisujemy. Jesli to
                // dokladnie to, co chcielismy, nie ma co zapisywac ani cofac.
                return CpuSetsSteer.Of(
                    SameSet(before.Ids, target)
                        ? CpuSetsSteerOutcome.NoChange
                        : CpuSetsSteerOutcome.SkippedForeign);
        }

        if (!port.Apply(processId, target))
        {
            return CpuSetsSteer.Of(CpuSetsSteerOutcome.NotApplied);
        }

        // Odczyt zwrotny: zapis, ktory system przyjal, nie musi byc tym,
        // co potem widac na procesie. Niezgodnosc to „nie zastosowano" i nie
        // wolamy cofania: bez zgodnosci z tym, co zapisalismy, aktualny stan
        // nie jest juz dowodnie nasz.
        //
        // Wyjatek: gdy odczytu zwrotnego nie da sie wykonac, nasz zapis
        // moze lezec na procesie bez sladu. Oryginal byl pusty (inaczej nie
        // doszloby do zapisu), wiec jedyny bezpieczny ruch to wrocic do
        // pustego. Niepusty odczyt z innymi ID jest cudzy i zostaje.
        CpuSetsQuery after = port.Query(processId);
        if (after.State == CpuSetsQueryState.Failed)
        {
            _ = port.Apply(processId, []);
            return CpuSetsSteer.Of(CpuSetsSteerOutcome.NotApplied);
        }

        return after.State == CpuSetsQueryState.NonEmpty
            && SameSet(after.Ids, target)
                ? new(CpuSetsSteerOutcome.Applied, before.Ids, [.. target])
                : CpuSetsSteer.Of(CpuSetsSteerOutcome.NotApplied);
    }

    internal static CpuSetsRestoreResult RestoreIfUnchanged(
        ICpuSetsPort port,
        int processId,
        IReadOnlyList<uint> original,
        IReadOnlyList<uint> applied)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(applied);

        CpuSetsQuery current = port.Query(processId);
        if (current.State == CpuSetsQueryState.Failed)
        {
            return CpuSetsRestoreResult.Failed;
        }

        // Porownanie jako zbiory: system moze zwrocic identyfikatory
        // w innej kolejnosci niz zapisane.
        if (!SameSet(current.Ids, applied))
        {
            return CpuSetsRestoreResult.LeftAlone;
        }

        return port.Apply(processId, original)
            ? CpuSetsRestoreResult.Restored
            : CpuSetsRestoreResult.Failed;
    }

    private static bool SameSet(IReadOnlyList<uint> left, IReadOnlyList<uint> right) =>
        left.ToHashSet().SetEquals(right);
}

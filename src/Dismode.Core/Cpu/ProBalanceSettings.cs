namespace Dismode.Core.Cpu;

/// <summary>
/// Thresholds for the reactive restraint loop.
/// <para>
/// These numbers are Dismode's own. Bitsum has never published ProBalance's
/// thresholds, windows or heuristics, so nothing here is derived from theirs —
/// it is a starting point chosen to be conservative, and the intent is to tune
/// it against measured frame times rather than to guess at someone else's
/// constants.
/// </para>
/// </summary>
public sealed record ProBalanceSettings
{
    /// <summary>
    /// How much processor the background has to be eating, in cores, before
    /// the loop will restrain anything.
    /// <para>
    /// This used to be machine-wide load as a percentage, and that was the
    /// wrong quantity twice over. It counted the game's own work as evidence
    /// that the machine was contended — the game's work is the thing being
    /// protected, not the interference — so the loop's behaviour depended on
    /// how heavy the game happened to be. A light game plus four busy
    /// background processes read 31% and was ignored; the same four
    /// background processes next to a heavy game read 41,7% and were acted on,
    /// though they were stealing exactly the same cores. And a percentage of
    /// the machine means something different on every machine, which is the
    /// mistake this file already avoids everywhere else.
    /// </para>
    /// <para>
    /// Counting only the background, in cores, removes both problems.
    /// Measured on the development machine: idle with a game running, the
    /// busiest background process used 0,07 of a core; four compute-bound
    /// background processes used 4,04 cores, and confining them moved the
    /// game's p99 frame time from 7,78 ms to 6,95 ms.
    /// </para>
    /// <para>
    /// Deliberately equal to <see cref="RestrainAboveCores"/>. Setting it
    /// higher looked reasonable and was incoherent: a lone process over the
    /// per-process threshold could never be acted on, because the background
    /// total is at least that process's own usage. Equal means the two rules
    /// agree — any process worth catching passes the gate on its own — and the
    /// gate still separates that from a quiet machine by a factor of ten.
    /// </para>
    /// </summary>
    public double BackgroundLoadCores { get; init; } = 0.75;

    /// <summary>
    /// How much CPU a process must be using to become a candidate, counted in
    /// cores rather than as a share of the machine. A share would mean the
    /// threshold quietly stops working on bigger hardware: one thread spinning
    /// flat out is 12.5% of an eight-thread box but 5% of a twenty-thread one,
    /// so the same process would be caught on one machine and ignored on the
    /// other. Cores are the same everywhere.
    /// </summary>
    public double RestrainAboveCores { get; init; } = 0.75;

    /// <summary>
    /// Lower than <see cref="RestrainAboveCores"/> on purpose. Releasing at the
    /// same number a process was caught at makes it flap in and out of
    /// restraint every other sample.
    /// </summary>
    public double ReleaseBelowCores { get; init; } = 0.35;

    /// <summary>Consecutive samples above the threshold before acting.</summary>
    public int SustainedSamples { get; init; } = 3;

    /// <summary>Consecutive samples below the release threshold before letting go.</summary>
    public int CalmSamples { get; init; } = 3;

    /// <summary>
    /// Restraint is held at least this long. A process caught mid-burst often
    /// dips for a sample or two without being finished.
    /// </summary>
    public TimeSpan MinimumRestraint { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// And never longer than this. A process that has been held down for two
    /// minutes is not a spike any more, and Dismode is not its scheduler.
    /// </summary>
    public TimeSpan MaximumRestraint { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Quiet period after release before the same process can be caught again.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many processes may be held back at once.
    /// <para>
    /// Without a cap, a busy machine hands the engine a dozen candidates at
    /// the same moment and it restrains all of them. That is not what keeps a
    /// game smooth — one or two genuine hogs are, and lowering everything else
    /// as well only spreads the disruption. Beyond that, each restraint is a
    /// journal write and a signature check, so an unbounded count turns a
    /// one-second tick into a long one.
    /// </para>
    /// </summary>
    public int MaximumRestrained { get; init; } = 3;

    /// <summary>
    /// Czy ograniczanemu procesowi obnizac takze priorytet wejscia-wyjscia.
    /// <para>
    /// Maska powinowactwa odbiera procesowi tla rdzenie, ale nie odbiera mu
    /// dysku: kopia zapasowa albo indeksowanie moze siedziec na dwoch
    /// rdzeniach i dalej zapychac kolejke odczytow, a gra czeka na swoje
    /// zasoby.
    /// </para>
    /// <para>
    /// Domyslnie wlaczone, bo mechanizm jest odwracalny, tani i sprawdzony
    /// odczytem. Zysk w czasie klatki pozostaje jednak niezmierzony na zywej
    /// sesji, dlatego stoi to tutaj jako przelacznik, a nie jako zaszyte
    /// zachowanie.
    /// </para>
    /// </summary>
    public bool LowerBackgroundIoPriority { get; init; } = true;

    /// <summary>
    /// Czy ograniczanemu procesowi wlaczac takze EcoQoS: Windows obniza mu
    /// taktowanie, a na procesorze hybrydowym kieruje go na rdzenie
    /// energooszczedne. Dziennikowane i cofane jak pozostale dzwignie.
    /// </summary>
    public bool ApplyEcoQos { get; init; }

    /// <summary>
    /// Odstep miedzy probkami, gdy tlo jest ponizej bramki i nic nie jest
    /// ograniczone.
    /// </summary>
    public TimeSpan CalmInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Odstep miedzy probkami, gdy tlo przekracza bramke albo cos jest
    /// ograniczone. Krotszy odstep lapie proces szybciej przy tej samej
    /// liczbie probek, a pelne przejscie kosztuje okolo 4,3 ms.
    /// </summary>
    public TimeSpan BusyInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ustawienia nastawione na najwyzsza wydajnosc gry, wybrane przez
    /// uzytkownika zamiast ostroznych wartosci domyslnych. Wszystkie dzwignie
    /// od pierwszego zlapania (priorytet, maska, EcoQoS, wejscie-wyjscie),
    /// nizszy prog, dwie probki zamiast trzech, gestsze probkowanie pod
    /// obciazeniem i wiecej procesow naraz. Progi nie sa zmierzone na czasie
    /// klatki; odwracalnosc i dziennik zostaja bez zmian.
    /// </summary>
    public static ProBalanceSettings Aggressive { get; } = new()
    {
        // Rowne z progiem procesu z tego samego powodu co w wartosciach
        // domyslnych: kazdy proces warty zlapania sam przechodzi bramke.
        BackgroundLoadCores = 0.5,
        RestrainAboveCores = 0.5,
        ReleaseBelowCores = 0.2,
        SustainedSamples = 2,
        CalmSamples = 5,
        MinimumRestraint = TimeSpan.FromSeconds(10),
        MaximumRestraint = TimeSpan.FromMinutes(10),
        Cooldown = TimeSpan.FromSeconds(10),
        MaximumRestrained = 8,
        LowerBackgroundIoPriority = true,
        ApplyEcoQos = true,
        CalmInterval = TimeSpan.FromSeconds(1),
        BusyInterval = TimeSpan.FromMilliseconds(500),
    };
}

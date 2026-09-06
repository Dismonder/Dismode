namespace GameShift.Core.Cpu;

/// <summary>
/// Thresholds for the reactive restraint loop.
/// <para>
/// These numbers are GameShift's own. Bitsum has never published ProBalance's
/// thresholds, windows or heuristics, so nothing here is derived from theirs —
/// it is a starting point chosen to be conservative, and the intent is to tune
/// it against measured frame times rather than to guess at someone else's
/// constants.
/// </para>
/// </summary>
public sealed record ProBalanceSettings
{
    /// <summary>
    /// Below this the machine is not actually contended, so restraining
    /// anything would cost responsiveness elsewhere and buy nothing.
    /// </summary>
    public double SystemLoadPercent { get; init; } = 70;

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
    /// minutes is not a spike any more, and GameShift is not its scheduler.
    /// </summary>
    public TimeSpan MaximumRestraint { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Quiet period after release before the same process can be caught again.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(30);
}

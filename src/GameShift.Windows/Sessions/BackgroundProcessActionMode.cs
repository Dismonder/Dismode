namespace GameShift.Windows.Sessions;

public enum BackgroundProcessActionMode
{
    CloseAndRestore = 1,
    LowerPriority = 2,

    /// <summary>
    /// BelowNormal and EcoQoS, and nothing else. Means exactly what its
    /// label has always said; saved rules from before the full bundle
    /// existed keep this meaning rather than being upgraded without consent.
    /// </summary>
    LowerPriorityAndEcoQos = 3,

    /// <summary>
    /// The full background bundle: BelowNormal, EcoQoS, the hard corner
    /// affinity mask, and lowered memory and I/O priority. The mask is the
    /// only CPU lever that moved frame times in measurement; the rest are
    /// reversible levers with a mechanism and no measured gain yet.
    /// </summary>
    RestrainBackground = 4,
}

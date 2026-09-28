namespace GameShift.Core.Cpu;

/// <summary>
/// Chooses how long the supervisor waits before the next sample. A calm
/// machine is sampled less often, so the loop costs little while nothing
/// happens; a busy background or an active restraint is sampled more often,
/// so a hog is caught and released sooner.
/// </summary>
public static class ProBalanceCadence
{
    public static TimeSpan NextInterval(
        ProBalanceSettings settings,
        double backgroundCores,
        int restrainedCount)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(restrainedCount);

        TimeSpan interval =
            restrainedCount > 0
                || backgroundCores >= settings.BackgroundLoadCores
                ? settings.BusyInterval
                : settings.CalmInterval;
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                interval,
                "Odstęp próbkowania musi być dodatni.");
        }

        return interval;
    }
}

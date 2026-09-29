namespace GameShift.Core.Cpu;

/// <summary>
/// Chooses how long the supervisor waits before the next sample. A calm
/// machine is sampled less often, so the loop costs little while nothing
/// happens; a busy background or an active restraint is sampled more often,
/// so a hog is caught and released sooner.
/// </summary>
public static class ProBalanceCadence
{
    // Task.Delay obcina do pelnych milisekund, wiec krotszy odstep daje
    // petle bez przerwy; dluzszego niz limit timera nie przyjmuje wcale.
    public static readonly TimeSpan MinimumInterval =
        TimeSpan.FromMilliseconds(1);
    public static readonly TimeSpan MaximumInterval =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static bool IsSupported(TimeSpan interval) =>
        interval >= MinimumInterval && interval <= MaximumInterval;

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
        if (!IsSupported(interval))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                interval,
                "Odstęp próbkowania musi mieścić się w zakresie timera.");
        }

        return interval;
    }
}

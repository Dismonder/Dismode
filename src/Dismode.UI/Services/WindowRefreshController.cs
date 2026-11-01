namespace Dismode.UI.Services;

internal sealed class WindowRefreshController(
    Action<bool> setTimerEnabled,
    Action refresh)
{
    internal bool IsActive { get; private set; }

    internal void Update(bool visible, bool minimized)
    {
        bool active = visible && !minimized;
        if (IsActive == active)
        {
            return;
        }

        IsActive = active;
        setTimerEnabled(active);
        Refresh();
    }

    internal void Refresh()
    {
        if (IsActive)
        {
            refresh();
        }
    }
}

internal static class SessionBackgroundPollPolicy
{
    internal static bool ShouldRunBackgroundTimer(bool windowVisible) =>
        // Inny klient moze uruchomic sesje takze przy wylaczonym automacie.
        !windowVisible;

    internal static TimeSpan GetBackgroundInterval(
        bool hasActiveSession,
        bool autoOptimizeEnabled,
        bool hasForegroundHook,
        TimeSpan watchingInterval,
        TimeSpan idleInterval) =>
        autoOptimizeEnabled && (!hasForegroundHook || hasActiveSession)
            ? watchingInterval
            : idleInterval;

}

internal sealed class SessionHostPollSchedule(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long? _lastCheck;

    internal bool ShouldQuery(
        bool hasActiveSession,
        bool endpointAvailable,
        bool forceCheck)
    {
        long now = _clock.GetTimestamp();
        if (!forceCheck && !hasActiveSession && endpointAvailable
            && _lastCheck is { } last
            && _clock.GetElapsedTime(last, now) < TimeSpan.FromSeconds(15))
        {
            return false;
        }

        _lastCheck = now;
        return true;
    }
}

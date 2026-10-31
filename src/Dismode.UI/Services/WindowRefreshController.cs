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
    internal static bool ShouldRunBackgroundTimer(
        bool windowVisible,
        bool hasActiveSession,
        bool endpointAvailable,
        bool autoOptimizeEnabled,
        int profileCount) =>
        !windowVisible
        && (hasActiveSession
            || !endpointAvailable
            || (autoOptimizeEnabled && profileCount > 0));

    internal static TimeSpan GetBackgroundInterval(
        bool hasActiveSession,
        bool autoOptimizeEnabled,
        bool hasForegroundHook,
        TimeSpan watchingInterval,
        TimeSpan idleInterval) =>
        autoOptimizeEnabled && (!hasForegroundHook || hasActiveSession)
            ? watchingInterval
            : idleInterval;

    internal static bool ShouldQuerySessionHost(
        bool hasActiveSession,
        bool endpointAvailable,
        bool forceCheck) =>
        forceCheck || hasActiveSession || !endpointAvailable;
}

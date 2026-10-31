using Dismode.UI.Services;

namespace Dismode.UnitTests;

[TestClass]
public sealed class WindowRefreshControllerTests
{
    [TestMethod]
    public void HiddenAndMinimizedWindowsStopTicksAndRefreshImmediatelyOnReturn()
    {
        bool timerRunning = false;
        int samples = 0;
        WindowRefreshController controller = new(
            enabled => timerRunning = enabled,
            () => samples++);

        controller.Update(visible: true, minimized: false);
        Assert.IsTrue(timerRunning);
        Assert.AreEqual(1, samples);
        controller.Update(visible: false, minimized: false);
        controller.Refresh();
        Assert.IsFalse(timerRunning);
        Assert.AreEqual(1, samples);
        controller.Update(visible: true, minimized: true);
        controller.Refresh();
        Assert.IsFalse(timerRunning);
        Assert.AreEqual(1, samples);
        controller.Update(visible: true, minimized: false);
        Assert.IsTrue(timerRunning);
        Assert.AreEqual(2, samples);
    }

    [TestMethod]
    public void DuplicateVisibilityEventsDoNotRestartTimersOrResample()
    {
        int timerChanges = 0;
        int samples = 0;
        WindowRefreshController controller = new(_ => timerChanges++, () => samples++);
        controller.Update(true, false);
        controller.Update(true, false);
        Assert.AreEqual(1, timerChanges);
        Assert.AreEqual(1, samples);
        controller.Refresh();
        Assert.AreEqual(2, samples);
        controller.Update(false, false);
        controller.Update(false, false);
        Assert.AreEqual(2, timerChanges);
        Assert.AreEqual(2, samples);
    }

    [TestMethod]
    public void BackgroundTimerStopsWhenIdleWithConfirmedEndpointAndNoWatchedGames()
    {
        Assert.IsFalse(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: true,
            hasActiveSession: true,
            endpointAvailable: true,
            autoOptimizeEnabled: true,
            profileCount: 3));
        Assert.IsFalse(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false,
            hasActiveSession: false,
            endpointAvailable: true,
            autoOptimizeEnabled: false,
            profileCount: 3));
        Assert.IsFalse(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false,
            hasActiveSession: false,
            endpointAvailable: true,
            autoOptimizeEnabled: true,
            profileCount: 0));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false,
            hasActiveSession: true,
            endpointAvailable: true,
            autoOptimizeEnabled: false,
            profileCount: 0));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false,
            hasActiveSession: false,
            endpointAvailable: false,
            autoOptimizeEnabled: false,
            profileCount: 0));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false,
            hasActiveSession: false,
            endpointAvailable: true,
            autoOptimizeEnabled: true,
            profileCount: 2));
    }

    [TestMethod]
    public void ForegroundHookAllowsLongerIdleIntervalAndSkipsRedundantHostPolls()
    {
        TimeSpan watching = TimeSpan.FromSeconds(6);
        TimeSpan idle = TimeSpan.FromSeconds(15);

        Assert.AreEqual(
            idle,
            SessionBackgroundPollPolicy.GetBackgroundInterval(
                hasActiveSession: false,
                autoOptimizeEnabled: true,
                hasForegroundHook: true,
                watching,
                idle));
        Assert.AreEqual(
            watching,
            SessionBackgroundPollPolicy.GetBackgroundInterval(
                hasActiveSession: false,
                autoOptimizeEnabled: true,
                hasForegroundHook: false,
                watching,
                idle));
        Assert.AreEqual(
            watching,
            SessionBackgroundPollPolicy.GetBackgroundInterval(
                hasActiveSession: true,
                autoOptimizeEnabled: true,
                hasForegroundHook: true,
                watching,
                idle));

        Assert.IsFalse(SessionBackgroundPollPolicy.ShouldQuerySessionHost(
            hasActiveSession: false,
            endpointAvailable: true,
            forceCheck: false));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldQuerySessionHost(
            hasActiveSession: true,
            endpointAvailable: true,
            forceCheck: false));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldQuerySessionHost(
            hasActiveSession: false,
            endpointAvailable: false,
            forceCheck: false));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldQuerySessionHost(
            hasActiveSession: false,
            endpointAvailable: true,
            forceCheck: true));
    }
}

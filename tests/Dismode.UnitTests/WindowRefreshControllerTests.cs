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
    public void BackgroundTimerKeepsFallbackForSessionsStartedByAnotherClient()
    {
        Assert.IsFalse(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: true));
        Assert.IsTrue(SessionBackgroundPollPolicy.ShouldRunBackgroundTimer(
            windowVisible: false));
    }

    [TestMethod]
    public void ForegroundHookAllowsLongerIdleInterval()
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

    }

    [TestMethod]
    public void IdleHostChecksResumeOnDeadlineShowSessionAndEndpointFailure()
    {
        ManualClock clock = new();
        SessionHostPollSchedule schedule = new(clock);
        Assert.IsTrue(schedule.ShouldQuery(false, true, false));
        Assert.IsFalse(schedule.ShouldQuery(false, true, false));
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.IsFalse(schedule.ShouldQuery(false, true, false));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(schedule.ShouldQuery(false, true, false));
        Assert.IsTrue(schedule.ShouldQuery(false, true, true));
        Assert.IsTrue(schedule.ShouldQuery(true, true, false));
        Assert.IsTrue(schedule.ShouldQuery(false, false, false));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}

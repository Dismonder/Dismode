using Dismode.Core.Interaction;

namespace Dismode.UnitTests;

[TestClass]
public sealed class EdgePanelStateTests
{
    [TestMethod]
    public void HoverRequiresDwellAndLeaveRequiresDelay()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.SetPointerInside(true);
        clock.Advance(249);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Opening, panel.State);
        clock.Advance(180);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        panel.SetPointerInside(false);
        clock.Advance(699);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closing, panel.State);
        clock.Advance(180);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
    }

    [TestMethod]
    public void EdgeStripCoversTheMiddleOfTheRightEdgeAndLeavesCornersFree()
    {
        EdgePanelRect strip = EdgePanelHotZone.EdgeStrip(new(0, 0, 2560, 1440));
        Assert.AreEqual(new EdgePanelRect(2558, 288, 2, 864), strip);
        Assert.IsTrue(strip.Contains(2559, 720));
        Assert.IsTrue(strip.Contains(2558, 288));
        Assert.IsFalse(strip.Contains(2557, 720));
        Assert.IsFalse(strip.Contains(2559, 100));
        Assert.IsFalse(strip.Contains(2559, 1400));
        Assert.AreEqual(
            new EdgePanelRect(-2, 216, 2, 648),
            EdgePanelHotZone.EdgeStrip(new(-1920, 0, 1920, 1080)));
    }

    [TestMethod]
    public void LeaveBoundsForgiveSmallOvershoot()
    {
        EdgePanelRect leave = new EdgePanelRect(2220, 296, 340, 800).Inflate(32);
        Assert.AreEqual(new EdgePanelRect(2188, 264, 404, 864), leave);
        Assert.IsTrue(leave.Contains(2188, 700));
        Assert.IsFalse(leave.Contains(2187, 700));
    }

    [TestMethod]
    public void AccidentalHoverAndReturnToPanelCancelPendingActions()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.SetPointerInside(true);
        clock.Advance(200);
        panel.SetPointerInside(false);
        clock.Advance(500);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        panel.Toggle();
        clock.Advance(180);
        panel.Tick();
        panel.SetPointerInside(true);
        panel.SetPointerInside(false);
        clock.Advance(300);
        panel.SetPointerInside(true);
        clock.Advance(500);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
    }

    [TestMethod]
    public void HotkeyReversesAnimationAndEscapeRequiresLeavingBeforeReopen()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.SetPointerInside(true);
        panel.Toggle();
        clock.Advance(80);
        panel.Tick();
        double visible = panel.Reveal;
        panel.Toggle();
        Assert.AreEqual(EdgePanelState.Closing, panel.State);
        Assert.AreEqual(visible, panel.Reveal);
        panel.Toggle();
        Assert.AreEqual(EdgePanelState.Opening, panel.State);
        panel.Close();
        clock.Advance(1000);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        clock.Advance(1000);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        panel.SetPointerInside(false);
        panel.SetPointerInside(true);
        clock.Advance(250);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Opening, panel.State);
    }

    [TestMethod]
    public void HotkeyOpenedPanelWaitsForAnActualPointerLeave()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.Toggle();
        clock.Advance(180);
        panel.Tick();
        clock.Advance(1000);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        panel.SetPointerInside(true);
        panel.SetPointerInside(false);
        clock.Advance(699);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closing, panel.State);
    }

    [TestMethod]
    public void TakenHotkeyDoesNotUnregisterAnotherOwnersHotkeyAndCanRetry()
    {
        bool available = false;
        int releases = 0;
        using EdgePanelHotkeyRegistration hotkey = new(() => available, () => releases++);
        Assert.IsFalse(hotkey.TryRegister());
        hotkey.Release();
        Assert.AreEqual(0, releases);
        available = true;
        Assert.IsTrue(hotkey.TryRegister());
        Assert.IsTrue(hotkey.TryRegister());
        hotkey.Release();
        hotkey.Release();
        Assert.AreEqual(1, releases);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}

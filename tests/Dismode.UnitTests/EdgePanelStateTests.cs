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
        clock.Advance(240);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        panel.SetPointerInside(false);
        clock.Advance(699);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closing, panel.State);
        clock.Advance(160);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
    }

    [TestMethod]
    public void HotZoneGeometryCoversRightmostTwoPixelsAndHandleWhileKeepingCornersFree()
    {
        EdgePanelRect monitor = new(0, 0, 2560, 1440);
        EdgePanelRect handle = new(2548, 672, 12, 96);
        EdgePanelRect strip = EdgePanelMetrics.GetHotStripBounds(monitor);
        Assert.AreEqual(new EdgePanelRect(2558, 288, 2, 864), strip);

        // Rogi pozostaja wolne dla przycisku zamykania i obszaru powiadomien.
        Assert.IsFalse(EdgePanelMetrics.IsInHotZone(2559, 0, monitor, handle));
        Assert.IsFalse(EdgePanelMetrics.IsInHotZone(2559, 287, monitor, handle));
        Assert.IsFalse(EdgePanelMetrics.IsInHotZone(2559, 1152, monitor, handle));
        Assert.IsFalse(EdgePanelMetrics.IsInHotZone(2559, 1439, monitor, handle));

        // Dwa skrajne piksele od 20% do 80% wysokosci otwieraja panel.
        Assert.IsTrue(EdgePanelMetrics.IsInHotZone(2558, 288, monitor, handle));
        Assert.IsTrue(EdgePanelMetrics.IsInHotZone(2559, 500, monitor, handle));
        Assert.IsTrue(EdgePanelMetrics.IsInHotZone(2558, 1151, monitor, handle));
        Assert.IsFalse(EdgePanelMetrics.IsInHotZone(2557, 400, monitor, handle));

        // Widoczny uchwyt dziala na calej szerokosci.
        Assert.IsTrue(EdgePanelMetrics.IsInHotZone(2548, 700, monitor, handle));

        // Po pierwszym wejsciu karta zachowuje margines 32 DIP.
        EdgePanelRect card = new(2086, 400, 450, 600);
        Assert.IsFalse(EdgePanelMetrics.IsInOpenPanelZone(2050, 500, card, monitor, handle, scale: 1.5, pointerEntered: false));
        Assert.IsTrue(EdgePanelMetrics.IsInOpenPanelZone(2050, 500, card, monitor, handle, scale: 1.5, pointerEntered: true));
        Assert.IsTrue(EdgePanelMetrics.IsInOpenPanelZone(2038, 500, card, monitor, handle, scale: 1.5, pointerEntered: true));
        Assert.IsFalse(EdgePanelMetrics.IsInOpenPanelZone(2037, 500, card, monitor, handle, scale: 1.5, pointerEntered: true));
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
        clock.Advance(240);
        panel.Tick();
        panel.SetPointerInside(true);
        panel.SetPointerInside(false);
        clock.Advance(650);
        panel.SetPointerInside(true);
        clock.Advance(800);
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
        clock.Advance(240);
        panel.Tick();
        clock.Advance(10000);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        Assert.IsFalse(panel.HasPointerEntered);
        panel.SetPointerInside(true);
        Assert.IsTrue(panel.HasPointerEntered);
        panel.SetPointerInside(false);
        clock.Advance(699);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closing, panel.State);
    }

    [TestMethod]
    public void DialogKeepsPanelOpenOnLeaveAndResetsLeaveGraceWhenDialogCloses()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.SetPointerInside(true);
        clock.Advance(250);
        panel.Tick();
        clock.Advance(240);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);

        panel.SetDialogOpen(true);
        panel.SetPointerInside(false);
        clock.Advance(5000);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        panel.Close();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        panel.Toggle();
        Assert.AreEqual(EdgePanelState.Open, panel.State);

        panel.SetDialogOpen(false);
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

    [TestMethod]
    public void ClosedPanelNeedsNoTimerExceptDuringHoverDwell()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        Assert.IsFalse(panel.NeedsTimer);
        panel.SetPointerInside(true);
        Assert.IsTrue(panel.NeedsTimer);
        panel.SetPointerInside(false);
        Assert.IsFalse(panel.NeedsTimer);
        panel.Toggle();
        Assert.IsTrue(panel.NeedsTimer);
        clock.Advance(240);
        panel.Tick();
        panel.SetPointerInside(true);
        panel.Close();
        clock.Advance(160);
        panel.Tick();
        Assert.IsFalse(panel.NeedsTimer);
        panel.SetPointerInside(false);
        panel.SetPointerInside(true);
        Assert.IsTrue(panel.NeedsTimer);
    }

    [TestMethod]
    public void DisabledAnimationsKeepHoverDelaysButCompleteTransitionsImmediately()
    {
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock) { AnimationsEnabled = false };
        panel.SetPointerInside(true);
        clock.Advance(249);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        clock.Advance(1);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Open, panel.State);
        Assert.AreEqual(1, panel.Reveal);
        panel.Close();
        Assert.AreEqual(EdgePanelState.Closed, panel.State);
        Assert.IsFalse(panel.NeedsTimer);
    }

    [TestMethod]
    [DataRow(-10, 0, 1d)]
    [DataRow(0, 0, 1d)]
    [DataRow(50, 50, 0.5d)]
    [DataRow(90, 90, 0.1d)]
    [DataRow(200, 90, 0.1d)]
    public void TransparencyClampsAndDoesNotPreventHover(int input, int expected, double opacity)
    {
        EdgePanelPreferences preferences = new(true, input);
        Assert.AreEqual(expected, preferences.HandleTransparencyPercent);
        Assert.AreEqual(opacity, preferences.HandleOpacity, 0.00001);
        FakeClock clock = new();
        EdgePanelStateMachine panel = new(clock);
        panel.SetPointerInside(EdgePanelMetrics.IsInHotZone(2559, 400,
            new(0, 0, 2560, 1440), new(2548, 672, 12, 96)));
        clock.Advance(250);
        panel.Tick();
        Assert.AreEqual(EdgePanelState.Opening, panel.State);
    }

    [TestMethod]
    public void OverlaySegmentsMapOffAndEveryStyleInVisualOrder()
    {
        Dismode.Core.Profiles.PerformanceOverlayStyle[] styles =
        [
            Dismode.Core.Profiles.PerformanceOverlayStyle.MinimalText,
            Dismode.Core.Profiles.PerformanceOverlayStyle.FpsAndLows,
            Dismode.Core.Profiles.PerformanceOverlayStyle.CompactBar,
            Dismode.Core.Profiles.PerformanceOverlayStyle.FullDeck,
        ];
        for (int i = 0; i < styles.Length; i++)
        {
            Assert.AreEqual(0, EdgePanelMetrics.GetOverlaySegment(false, styles[i]));
            Assert.AreEqual(i + 1, EdgePanelMetrics.GetOverlaySegment(true, styles[i]));
            Assert.AreEqual(styles[i], EdgePanelMetrics.GetOverlayStyle(i + 1));
        }
        Assert.IsNull(EdgePanelMetrics.GetOverlayStyle(0));
        Assert.IsNull(EdgePanelMetrics.GetOverlayStyle(5));
    }

    [TestMethod]
    public void FpsHistoryUsesTimeWindowAndFixedCapacityAndRejectsInvalidReadings()
    {
        FakeClock clock = new();
        EdgePanelMetrics history = new(clock);
        history.Add(100);
        history.Add(double.NaN);
        history.Add(null);
        clock.Advance(59000);
        history.Add(120);
        Assert.AreEqual(2, history.Read().Count);
        clock.Advance(1001);
        Assert.AreEqual(120d, history.Read().Single().Fps);
        for (int i = 0; i < 1000; i++)
        {
            history.Add(i);
        }
        Assert.IsTrue(history.Read().Count <= EdgePanelMetrics.Capacity);
        clock.Advance(60001);
        Assert.AreEqual(0, history.Read().Count);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }
}

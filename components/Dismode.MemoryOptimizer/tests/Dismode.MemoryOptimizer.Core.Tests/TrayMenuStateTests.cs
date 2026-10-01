using System.Globalization;
using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryOptimizer.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class TrayMenuStateTests
{
    [TestMethod]
    public void DisconnectedServiceDoesNotExposeStaleMetricsOrActions()
    {
        TrayMenuState state = TrayMenuState.Create(null, true, true, CultureInfo.InvariantCulture);
        Assert.IsNull(state.Percent);
        Assert.IsFalse(state.CanOptimize);
        Assert.IsFalse(state.CanPause);
        Assert.AreEqual("—", state.AvailableText);
    }

    [TestMethod]
    public void BusyStatePreventsAnotherOptimizationEvenIfCallerAllowsIt()
    {
        TrayMenuState state = TrayMenuState.Create(Status() with { IsOptimizationRunning = true }, true, true, CultureInfo.InvariantCulture);
        Assert.IsTrue(state.IsBusy);
        Assert.IsFalse(state.CanOptimize);
    }

    [TestMethod]
    public void ManualOnlyModeDoesNotPretendAutomationIsActive()
    {
        MemoryOptimizerStatus status = Status();
        TrayMenuState state = TrayMenuState.Create(status with
        {
            Settings = status.Settings with { AutomationEnabled = false, ScheduleEnabled = false },
        }, true, true, CultureInfo.InvariantCulture);
        Assert.AreEqual("Tryb ręczny", state.StatusText);
        Assert.IsTrue(state.CanOptimize);
        Assert.IsFalse(state.CanPause);
    }

    [TestMethod]
    public void PausedAutomationStillAllowsManualActionAndUsesPhysicalRam()
    {
        TrayMenuState state = TrayMenuState.Create(Status() with { IsPaused = true }, true, true, CultureInfo.InvariantCulture);
        Assert.AreEqual("Wznów", state.PauseText);
        Assert.IsTrue(state.CanOptimize);
        Assert.AreEqual("4.0 GB", state.AvailableText);
        Assert.AreEqual("12.0 GB z 16.0 GB", state.UsageText);
    }

    [TestMethod]
    public void BlockedResultIsReportedWithoutClaimingMemoryWasRecovered()
    {
        MemoryOptimizerStatus status = Status();
        OptimizationResult result = new(Guid.NewGuid(), OptimizationTrigger.Manual,
            MemoryOptimizerSettings.BasicAreas, OptimizationState.Blocked,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, status.Memory, status.Memory,
            [], "Aktywna gra");
        TrayMenuState state = TrayMenuState.Create(status with { LastResult = result }, true, true, CultureInfo.InvariantCulture);
        StringAssert.Contains(state.DetailText, "zablokowana");
        StringAssert.Contains(state.DetailText, "Aktywna gra");
        Assert.IsFalse(state.DetailText.Contains("MB", StringComparison.Ordinal));
    }

    private static MemoryOptimizerStatus Status() => new(false, false,
        new(DateTimeOffset.UtcNow, 16UL << 30, 4UL << 30, 128UL << 40, 127UL << 40, 75),
        new(), null, "0.4.0", "");
}

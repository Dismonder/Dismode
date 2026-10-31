using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Journal;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Windows.Display;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class DisplayRefreshRateTests
{
    [TestMethod]
    public async Task UnmappableVisibleWindowFallsBackToPrimaryWithoutChangingSecondMonitor()
    {
        FakeDisplay api = new() { MapWindow = false };
        await using SyntheticSession session = new();
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api)!;
        Assert.AreEqual("primary", action.Target.DeviceName);
        ActionExecutionContext context = new(session.Id, action.Descriptor.ActionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(), DateTimeOffset.UtcNow);
        await new TransactionCoordinator<DisplayMode>(session.Journal).ExecuteAsync(action, context, CancellationToken.None);
        Assert.AreEqual(360, api.Primary.RefreshRate);
        Assert.AreEqual(60, api.Current.RefreshRate);
        await new ActionRecoveryCoordinator<DisplayMode>(session.Journal).RecoverAsync(action, context, CancellationToken.None);
        Assert.AreEqual(60, api.Primary.RefreshRate);
        Assert.AreEqual(60, api.Current.RefreshRate);
    }

    [TestMethod]
    public async Task NoWindowDuringEntireSessionChangesNothing()
    {
        FakeDisplay api = new() { HasWindow = false };
        await using SyntheticSession session = new(api);
        await session.StartAsync(refreshRatePending: true);
        await Task.Delay(60);
        Assert.AreEqual(0, api.SetCount);
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(0, api.SetCount);
    }

    [TestMethod]
    public async Task SecondaryMonitorIsChangedOnlyAfterDescendantWindowAppears()
    {
        FakeDisplay api = new() { HasWindow = false, WindowProcessId = 12346 };
        await using SyntheticSession session = new(api);
        await session.StartAsync(refreshRatePending: true);
        await Task.Delay(40);
        Assert.AreEqual(0, api.SetCount);
        session.Clock.Advance(1);
        session.Parents[12346] = session.Root.RuntimeKey.ProcessId;
        session.Running.Add(new(new(12346, session.Clock.GetUtcNow()), session.Root.ExecutablePath,
            session.Root.ExecutableSha256, null, "user", 1, session.Root.RuntimeKey));
        api.HasWindow = true;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (api.SetCount == 0) { await Task.Delay(5, timeout.Token); }
        Assert.AreEqual(144, api.Current.RefreshRate);
        Assert.AreEqual(60, api.Primary.RefreshRate);
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(60, api.Current.RefreshRate);
        Assert.AreEqual(60, api.Primary.RefreshRate);
    }

    [TestMethod]
    public void TinyWindowsAreNotUsableGameWindows()
    {
        Assert.IsFalse(WindowsDisplayApi.HasUsableWindowArea(1, 1));
        Assert.IsFalse(WindowsDisplayApi.HasUsableWindowArea(1920, 10));
        Assert.IsTrue(WindowsDisplayApi.HasUsableWindowArea(1920, 1080));
    }

    [TestMethod]
    public async Task GameRestoringOriginalModeIsJournaledAsRestoredWithoutConflict()
    {
        await using SyntheticSession session = new();
        FakeDisplay api = new();
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api)!;
        DisplayMode original = api.Current;
        ActionExecutionContext context = new(session.Id, action.Descriptor.ActionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(), DateTimeOffset.UtcNow);
        await new TransactionCoordinator<DisplayMode>(session.Journal).ExecuteAsync(action, context, CancellationToken.None);
        api.Current = original;
        var result = await new ActionRecoveryCoordinator<DisplayMode>(session.Journal)
            .RecoverAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.AreEqual(1, api.SetCount);
        Assert.AreEqual(JournalEventKind.CompensationVerified,
            (await session.Journal.ReadAllAsync(CancellationToken.None))[^1].EventKind);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrchestratorRestoresDisplayAtSessionEndAndOnCrashReplay(bool gameExited)
    {
        FakeDisplay api = new();
        await using SyntheticSession session = new(api);
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api)!;
        var key = Dismode.Contracts.Protocol.IdempotencyKey.Create();
        ActionExecutionContext context = new(session.Id, action.Descriptor.ActionId, key, DateTimeOffset.UtcNow);
        await new TransactionCoordinator<DisplayMode>(session.Journal).ExecuteAsync(action, context, CancellationToken.None);
        if (gameExited)
        {
            session.Running.Clear();
        }
        await session.StartAsync(new
        {
            ActionId = action.Descriptor.ActionId.Value,
            IdempotencyKey = key.Value,
            Target = action.Target
        });
        Assert.AreEqual(60, api.Current.RefreshRate);
        if (!gameExited)
        {
            await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        }
        Assert.AreEqual(60, api.Current.RefreshRate);
        Assert.AreEqual(2, api.SetCount);
    }

    [TestMethod]
    public void PicksMaxAtCurrentResolutionAndBitDepthOnGameMonitor()
    {
        FakeDisplay api = new();
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api);
        Assert.IsNotNull(action);
        Assert.AreEqual("second", action.Target.DeviceName);
        Assert.AreEqual(144, action.Target.RefreshRate);
        Assert.AreEqual(0, api.SetCount);
        api.Current = api.Current with { RefreshRate = 144 };
        Assert.IsNull(DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api));
    }

    [TestMethod]
    public void UnavailableUserSessionAndMissingMonitorAreNoOps()
    {
        FakeDisplay api = new() { Available = false };
        Assert.IsNull(DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api));
        api.Available = true;
        api.Missing = true;
        Assert.IsNull(DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api));
    }

    [TestMethod]
    public async Task JournalsBeforeChangingAndCrashReplayRestoresOriginalMode()
    {
        await using SyntheticSession session = new();
        FakeDisplay api = new();
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api)!;
        ActionExecutionContext context = new(session.Id, action.Descriptor.ActionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(), DateTimeOffset.UtcNow);
        api.BeforeSet = () => Assert.IsTrue(session.Journal.ReadAllAsync(CancellationToken.None)
            .AsTask().GetAwaiter().GetResult().Any(entry => entry.EventKind == JournalEventKind.ActionApplying));
        var result = await new TransactionCoordinator<DisplayMode>(session.Journal)
            .ExecuteAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, result.Status);
        Assert.AreEqual(144, api.Current.RefreshRate);
        var replay = DisplayRefreshRateAction.ForRecovery(action.Descriptor.ActionId, action.Target, api);
        var restored = await new ActionRecoveryCoordinator<DisplayMode>(session.Journal)
            .RecoverAsync(replay, context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(60, api.Current.RefreshRate);
        Assert.AreEqual(2, api.SetCount);
    }

    [TestMethod]
    public async Task UserModeChangeIsPreservedDuringRecovery()
    {
        await using SyntheticSession session = new();
        FakeDisplay api = new();
        var action = DisplayRefreshRateAction.TryCreate(ActionId.Create(), [42], api)!;
        ActionExecutionContext context = new(session.Id, action.Descriptor.ActionId,
            Dismode.Contracts.Protocol.IdempotencyKey.Create(), DateTimeOffset.UtcNow);
        await new TransactionCoordinator<DisplayMode>(session.Journal).ExecuteAsync(action, context, CancellationToken.None);
        api.Current = api.Current with { RefreshRate = 120 };
        var restored = await new ActionRecoveryCoordinator<DisplayMode>(session.Journal)
            .RecoverAsync(action, context, CancellationToken.None);
        Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, restored.Status);
        Assert.AreEqual(120, api.Current.RefreshRate);
        Assert.AreEqual(1, api.SetCount);
    }

    private sealed class FakeDisplay : IDisplayApi
    {
        internal DisplayMode Current { get; set; } = new("second", "monitor2", 1920, 1080, 32, 60);
        internal DisplayMode Primary { get; set; } = new("primary", "monitor1", 1920, 1080, 32, 60);
        internal bool Available { get; set; } = true;
        internal bool HasWindow { get; set; } = true;
        internal bool MapWindow { get; set; } = true;
        internal int WindowProcessId { get; set; } = 42;
        internal bool Missing { get; set; }
        internal int SetCount { get; private set; }
        internal Action? BeforeSet { get; set; }
        public bool IsUserSession => Available;
        public string? FindGameMonitor(IReadOnlyList<int> processIds) =>
            HasWindow && processIds.Contains(WindowProcessId) ? MapWindow ? "second" : "primary" : null;
        public DisplayMode? GetCurrentMode(string deviceName) => Missing ? null : deviceName == "primary" ? Primary : Current;
        public IReadOnlyList<DisplayMode> GetModes(string deviceName) =>
        [Current, Current with { RefreshRate = 144 }, Current with { Width = 1280, RefreshRate = 240 },
            Current with { BitsPerPixel = 16, RefreshRate = 240 }, new("primary", "monitor1", 1920, 1080, 32, 360)];
        public void SetMode(DisplayMode mode)
        {
            BeforeSet?.Invoke();
            if (mode.DeviceName == "primary") { Primary = mode; }
            else { Current = mode; }
            SetCount++;
        }
    }
}

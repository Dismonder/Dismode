using System.Diagnostics;
using System.Reflection;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Profiles;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class LateBackgroundRuleTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateRuleFailureIsLoggedDisablesFeatureAndDoesNotStopExitMonitoring(bool unexpected)
    {
        await using SyntheticSession session = new(lateRules: true,
            journalDecorator: journal => new FailLateCheckpointOnce(journal, unexpected));
        await session.StartAsync();
        session.Clock.Advance(1);
        session.Running.Add(Background(12346, session.Clock.GetUtcNow()));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.LateErrors.IsEmpty) { await Task.Delay(5, timeout.Token); }
        Assert.AreEqual(0, session.PrioritySetCount);
        session.Clock.Advance(1);
        session.Running.Add(Background(12347, session.Clock.GetUtcNow()));
        await Task.Delay(50, timeout.Token);
        Assert.AreEqual(0, session.PrioritySetCount);
        session.Running.Clear();
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        session.Clock.Advance(30);
        await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
    }

    [TestMethod]
    public async Task UnchangedProcessSetSkipsFullInventoryButNewBirthAtReusedPidTriggersIt()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        using CancellationTokenSource ready = new(TimeSpan.FromSeconds(5));
        while (session.InventoryCaptureCount == 0) { await Task.Delay(5, ready.Token); }
        int captures = session.InventoryCaptureCount;
        await Task.Delay(70);
        Assert.AreEqual(captures, session.InventoryCaptureCount);
        session.Clock.Advance(1);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow());
        session.Running.Add(background);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount < 1) { await Task.Delay(5, timeout.Token); }
        session.Running.Clear();
        session.Running.Add(session.Root);
        session.Clock.Advance(1);
        session.Running.Add(Background(12346, session.Clock.GetUtcNow()));
        while (session.PrioritySetCount < 2) { await Task.Delay(5, timeout.Token); }
        Assert.IsGreaterThan(captures, session.InventoryCaptureCount);
    }

    [TestMethod]
    public async Task ProcessStartedBetweenPlanResolutionAndActivationReceivesRule()
    {
        await using SyntheticSession session = new(lateRules: true);
        DateTimeOffset resolvedAt = session.Clock.GetUtcNow().AddSeconds(-5);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow().AddSeconds(-2));
        session.Running.Add(background);
        await session.StartAsync(lateRulesResolvedAtUtc: resolvedAt);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount == 0) { await Task.Delay(5, timeout.Token); }
        Assert.AreEqual(ProcessPriorityClass.BelowNormal, session.Priorities[background.RuntimeKey]);
    }

    [TestMethod]
    public async Task ForegroundProcessIsAttemptedAndNeverRestrainedAfterLeavingForeground()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow());
        session.ForegroundPid = 12346;
        session.Running.Add(background);
        await session.WaitForCheckpointAsync(SessionCheckpoint.BackgroundRestraintChanged);
        session.ForegroundPid = 0;
        session.Clock.Advance(1);
        session.Running.Add(new(new(12347, session.Clock.GetUtcNow()), @"C:\Apps\unmatched.exe",
            new string('C', 64), null, "user", 1, null));
        await Task.Delay(80);
        Assert.AreEqual(0, session.PrioritySetCount);
    }

    [TestMethod]
    public async Task LateClaimWinsRaceWithProBalanceAndRefusedLedgerNeverAppliesAnything()
    {
        await using SyntheticSession session = new(lateRules: true);
        TaskCompletionSource claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.BeforeLateApproval = async () => { claimed.TrySetResult(); await proceed.Task; };
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow());
        session.Running.Add(background);
        try
        {
            await claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            JournaledProBalanceActuator actuator = new(session.Journal, session.Id, session,
                lowerBackgroundIoPriority: false)
            { CanLowerPriority = _ => true };
            actuator.AttachLedger(Ledger(session));
            Assert.IsFalse(await actuator.RestrainAsync(background.RuntimeKey, CancellationToken.None));
            Assert.IsFalse((await session.Journal.ReadAllAsync(CancellationToken.None))
                .Any(entry => entry.EventKind == JournalEventKind.ActionApplying));
            Assert.AreEqual(0, session.PrioritySetCount);
        }
        finally { proceed.TrySetResult(); }
    }

    [TestMethod]
    public async Task ProBalanceLedgerWinsRaceBeforeLateClaim()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow());
        IRestraintLedger ledger = Ledger(session);
        await ledger.RecordAsync(new(background, "ProBalance", ActionId.Create(), IdempotencyKey.Create(),
            null, null, null, null, null, null), CancellationToken.None);
        session.Running.Add(background);
        await Task.Delay(70);
        Assert.AreEqual(0, session.PrioritySetCount);
        await ledger.ForgetAsync(background.RuntimeKey, CancellationToken.None);
    }

    [TestMethod]
    public async Task ProBalanceCheckpointFailureCannotReachAnUnjournaledMutation()
    {
        await using SyntheticSession session = new(journalDecorator: journal => new FailLateCheckpointOnce(journal, false));
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = Background(12346, session.Clock.GetUtcNow());
        session.Running.Add(background);
        JournaledProBalanceActuator actuator = new(session.Journal, session.Id, session,
            lowerBackgroundIoPriority: false)
        { CanLowerPriority = _ => true };
        actuator.AttachLedger(Ledger(session));
        Assert.IsFalse(await actuator.RestrainAsync(background.RuntimeKey, CancellationToken.None));
        Assert.IsFalse((await session.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.EventKind == JournalEventKind.ActionApplying));
        Assert.AreEqual(0, session.PrioritySetCount);
    }

    private static IRestraintLedger Ledger(SyntheticSession session)
    {
        object runtime = typeof(LocalGameSessionOrchestrator).GetField("_activeSession",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session.Orchestrator)!;
        return (IRestraintLedger)Activator.CreateInstance(typeof(LocalGameSessionOrchestrator)
            .GetNestedType("RuntimeRestraintLedger", BindingFlags.NonPublic)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [session.Orchestrator, runtime], null)!;
    }

    private static ProcessIdentity Background(int pid, DateTimeOffset started) =>
        new(new(pid, started), @"C:\Apps\background.exe", new string('B', 64), null, "user", 1, null);

    private sealed class FailLateCheckpointOnce(IRecoveryJournal inner, bool unexpected) : IRecoveryJournal
    {
        private bool _failed;
        public ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllAsync(CancellationToken token) => inner.ReadAllAsync(token);
        public ValueTask<RecoveryJournalEntry> AppendDurableAsync(RecoveryJournalDraft draft, CancellationToken token)
        {
            if (!_failed && draft.SessionCheckpoint == SessionCheckpoint.BackgroundRestraintChanged)
            {
                _failed = true;
                throw unexpected ? new LateRuleFaultException() : new IOException("late journal fault");
            }
            return inner.AppendDurableAsync(draft, token);
        }
    }

    private sealed class LateRuleFaultException : Exception;

    [TestMethod]
    public async Task OldMatchingProcessesDoNotHideNewProcessesBehindTheSelectionLimit()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        DateTimeOffset oldStart = session.Clock.GetUtcNow().AddSeconds(-1);
        for (int pid = 10000; pid < 10064; pid++)
        {
            session.Running.Add(new(new(pid, oldStart), @"C:\Apps\background.exe",
                new string('B', 64), null, "user", 1, null));
        }
        session.Clock.Advance(1);
        ProcessIdentity fresh = new(new(12346, session.Clock.GetUtcNow()),
            @"C:\Apps\background.exe", new string('B', 64), null, "user", 1, null);
        session.Running.Add(fresh);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount == 0) { await Task.Delay(5, timeout.Token); }
        Assert.AreEqual(ProcessPriorityClass.BelowNormal, session.Priorities[fresh.RuntimeKey]);
        Assert.AreEqual(1, session.PrioritySetCount);
    }

    [TestMethod]
    public async Task CrashReplayRestoresRuleAndDoesNotApplyItAgainToTheSameIdentity()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = new(new(12346, session.Clock.GetUtcNow()),
            @"C:\Apps\background.exe", new string('B', 64), null, "user", 1, null);
        session.Running.Add(background);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount == 0) { await Task.Delay(5, timeout.Token); }
        await session.Orchestrator.DisposeAsync();
        await using LocalGameSessionOrchestrator recovery = new(session.Store, session.Store,
            session.Journal, identityProvider: session, processParentMapProvider: session,
            processInventory: session, savedRuleResolver: new(session),
            cpuProcessSourceFactory: () => session,
            timeProvider: session.Clock, gameplayClock: session.Clock,
            monitorInterval: TimeSpan.FromMilliseconds(5))
        {
            ForegroundProcessId = session.Orchestrator.ForegroundProcessId,
            LateRuleApprover = session.Orchestrator.LateRuleApprover,
            BackgroundPriorityActionFactory = session.Orchestrator.BackgroundPriorityActionFactory,
        };
        await recovery.InitializeAsync(CancellationToken.None);
        Assert.AreEqual(ProcessPriorityClass.Normal, session.Priorities[background.RuntimeKey]);
        await Task.Delay(60, timeout.Token);
        Assert.AreEqual(1, session.PrioritySetCount);
        session.Clock.Advance(1);
        ProcessIdentity restarted = new(new(12346, session.Clock.GetUtcNow()),
            background.ExecutablePath, background.ExecutableSha256, null, "user", 1, null);
        session.Running.Clear();
        session.Running.Add(session.Root);
        session.Running.Add(restarted);
        while (session.PrioritySetCount < 2) { await Task.Delay(5, timeout.Token); }
        await recovery.RestoreAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(ProcessPriorityClass.Normal, session.Priorities[restarted.RuntimeKey]);
    }

    [TestMethod]
    public async Task MonitorAppliesLateRuleOnceJournalsAndRestoresIt()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = new(new(12346, session.Clock.GetUtcNow()),
            @"C:\Apps\background.exe", new string('B', 64), null, "user", 1, null);
        session.Running.Add(background);
        await session.WaitForCheckpointAsync(SessionCheckpoint.BackgroundRestraintChanged);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount == 0) { await Task.Delay(5, timeout.Token); }
        await Task.Delay(50, timeout.Token);
        Assert.AreEqual(1, session.PrioritySetCount);
        Assert.AreEqual(ProcessPriorityClass.BelowNormal, session.Priorities[background.RuntimeKey]);
        var entries = await session.Journal.ReadAllAsync(CancellationToken.None);
        Assert.HasCount(1, entries.Where(entry => entry.EventKind == JournalEventKind.ActionApplying).ToArray());
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(ProcessPriorityClass.Normal, session.Priorities[background.RuntimeKey]);
    }

    [TestMethod]
    public async Task MonitorPreservesExternalChange()
    {
        await using SyntheticSession session = new(lateRules: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity background = new(new(12346, session.Clock.GetUtcNow()),
            @"C:\Apps\background.exe", new string('B', 64), null, "user", 1, null);
        session.Running.Add(background);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (session.PrioritySetCount == 0) { await Task.Delay(5, timeout.Token); }
        session.Priorities[background.RuntimeKey] = ProcessPriorityClass.AboveNormal;
        await Task.Delay(80, timeout.Token);
        Assert.AreEqual(1, session.PrioritySetCount);
        await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None);
        Assert.AreEqual(ProcessPriorityClass.AboveNormal, session.Priorities[background.RuntimeKey]);
    }

    [TestMethod]
    public async Task SettingDefaultsOffAndRoundTripsWithOtherSwitches()
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        GameProfileId id = session.ProfileId;
        var initial = await session.Store.LoadOptimizationPreferencesAsync(id, CancellationToken.None);
        Assert.IsFalse(initial.ApplyBackgroundRulesDuringSession);
        await session.Store.SaveOptimizationPreferencesAsync(new(id, SavedGamePriorityMode.Normal,
            [], DateTimeOffset.UtcNow, applyBackgroundRulesDuringSession: true), CancellationToken.None);
        await session.Store.SetKeepAwakeForProfileAsync(id, false, CancellationToken.None);
        Assert.IsTrue((await session.Store.LoadOptimizationPreferencesAsync(id, CancellationToken.None))
            .ApplyBackgroundRulesDuringSession);
    }

    [TestMethod]
    public void AppliesOncePerIdentityAndNeverReappliesAfterExternalChange()
    {
        DateTimeOffset activation = DateTimeOffset.UnixEpoch;
        LateBackgroundRuleTracker tracker = new(activation);
        ProcessIdentity process = Identity(2, activation.AddSeconds(1));
        Assert.IsTrue(tracker.TryClaim(process, [], 0));
        Assert.IsFalse(tracker.TryClaim(process, [], 0));
        Assert.IsTrue(tracker.TryClaim(Identity(2, activation.AddSeconds(2)), [], 0));
        for (int pid = 3; pid < 600; pid++)
        {
            Assert.IsTrue(tracker.TryClaim(Identity(pid, activation.AddSeconds(3)), [], 0));
        }
    }

    [TestMethod]
    public void SkipsOldForegroundAndGameTreeProcesses()
    {
        DateTimeOffset activation = DateTimeOffset.UnixEpoch;
        LateBackgroundRuleTracker tracker = new(activation);
        ProcessIdentity fresh = Identity(2, activation.AddSeconds(1));
        Assert.IsFalse(tracker.TryClaim(Identity(1, activation), [], 0));
        Assert.IsFalse(tracker.TryClaim(fresh, [fresh], 0));
        Assert.IsFalse(tracker.TryClaim(fresh, [], 2));
        Assert.IsFalse(tracker.TryClaim(fresh, [], 0));
    }

    private static ProcessIdentity Identity(int pid, DateTimeOffset started) =>
        new(new(pid, started), @"C:\background.exe", new string('A', 64), null, "user", 1, null);
}

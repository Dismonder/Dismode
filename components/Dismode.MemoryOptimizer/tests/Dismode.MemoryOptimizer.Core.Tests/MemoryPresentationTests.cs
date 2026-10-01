using Dismode.MemoryOptimizer.Core.Models;
using Dismode.MemoryOptimizer.Presentation;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryPresentationTests
{
    [TestMethod]
    public void PollingPreservesDraftButMergesExternalPause()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { AvailableMemoryThresholdPercent = 35 });

        draft.Receive(draft.Baseline with { AutomationPaused = true });

        Assert.AreEqual(35, draft.Current.AvailableMemoryThresholdPercent);
        Assert.IsTrue(draft.Current.AutomationPaused);
        Assert.IsTrue(draft.HasChanges);
    }

    [TestMethod]
    public void ReceivingUnchangedSettingsDoesNotRequestAnotherControlLayoutPass()
    {
        MemorySettingsDraft draft = new();

        Assert.IsTrue(draft.Receive(new()));
        Assert.IsFalse(draft.Receive(draft.Baseline));
        Assert.IsTrue(draft.Receive(
            draft.Baseline with { CooldownMinutes = 60 }));
    }

    [TestMethod]
    public void InvalidControlInputRemainsDirtyUntilDiscardedOrCorrected()
    {
        MemorySettingsDraft draft = CreateDraft();

        draft.MarkInvalidInput();
        draft.Receive(draft.Baseline with { AutomationPaused = true });

        Assert.IsTrue(draft.HasInvalidInput);
        Assert.IsTrue(draft.HasChanges,
            "Polling must not make an incomplete NumberBox look clean.");
        draft.Discard();
        Assert.IsFalse(draft.HasInvalidInput);
        Assert.IsFalse(draft.HasChanges);
    }

    [TestMethod]
    public void CorrectedControlInputClearsTheInvalidMarker()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.MarkInvalidInput();

        draft.Edit(draft.Current with { AvailableMemoryThresholdPercent = 35 });

        Assert.IsFalse(draft.HasInvalidInput);
        Assert.IsTrue(draft.HasChanges);
    }

    [TestMethod]
    public void RecoveredServiceFeedbackClearsOnlyConnectionErrors()
    {
        MemoryFeedbackState feedback = new();
        feedback.ShowError("offline", isServiceConnectionError: true);

        Assert.IsTrue(feedback.ClearRecoveredServiceError());
        Assert.IsNull(feedback.Message);

        feedback.ShowError("save failed", isServiceConnectionError: false);
        Assert.IsFalse(feedback.ClearRecoveredServiceError());
        Assert.AreEqual("save failed", feedback.Message);
    }

    [TestMethod]
    public void RevertingAnEditClearsDirtyStateAndExclusionOrderIsNotADifference()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { CooldownMinutes = 60 });
        Assert.IsTrue(draft.HasChanges);
        draft.Edit(draft.Current with { CooldownMinutes = 30 });
        Assert.IsFalse(draft.HasChanges);
        draft.Receive(draft.Baseline with { ExcludedProcesses = ["alpha", "beta"] });
        draft.Edit(draft.Current with { ExcludedProcesses = ["BETA", "alpha"] });
        Assert.IsFalse(draft.HasChanges);
    }

    [TestMethod]
    public async Task FailedSaveKeepsTheUnsavedDraft()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { CooldownMinutes = 60 });
        await Assert.ThrowsExactlyAsync<IOException>(() => draft.SaveAsync(
            (_, _) => Task.FromException<MemoryOptimizerSettings>(new IOException("offline")),
            CancellationToken.None));

        Assert.AreEqual(60, draft.Current.CooldownMinutes);
        Assert.AreEqual(30, draft.Baseline.CooldownMinutes);
        Assert.IsTrue(draft.HasChanges);
        Assert.IsFalse(draft.IsSaving);
    }

    [TestMethod]
    public async Task EditDuringSaveSurvivesTheLateReply()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { CooldownMinutes = 60 });
        TaskCompletionSource<MemoryOptimizerSettings> reply = new();
        Task<bool> saving = draft.SaveAsync((_, _) => reply.Task, CancellationToken.None);
        draft.Edit(draft.Current with { CooldownMinutes = 90 });
        reply.SetResult(new() { CooldownMinutes = 60 });
        Assert.IsTrue(await saving);

        Assert.AreEqual(60, draft.Baseline.CooldownMinutes);
        Assert.AreEqual(90, draft.Current.CooldownMinutes);
        Assert.IsTrue(draft.HasChanges);
        draft.Discard();
        Assert.AreEqual(60, draft.Current.CooldownMinutes);
        Assert.IsFalse(draft.HasChanges);
    }

    [TestMethod]
    public async Task WindowPreferenceSaveNeverPersistsUnsavedRules()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { AvailableMemoryThresholdPercent = 40 });
        MemoryOptimizerSettings? sent = null;
        Assert.IsTrue(await draft.SaveWindowPreferencesAsync(
            compact: true,
            alwaysOnTop: true,
            (settings, _) =>
            {
                sent = settings;
                return Task.FromResult(settings);
            },
            CancellationToken.None));

        Assert.IsNotNull(sent);
        Assert.AreEqual(20, sent.AvailableMemoryThresholdPercent);
        Assert.IsTrue(sent.CompactMode);
        Assert.IsTrue(sent.CompactAlwaysOnTop);
        Assert.AreEqual(40, draft.Current.AvailableMemoryThresholdPercent);
        Assert.IsTrue(draft.HasChanges);
    }

    [TestMethod]
    public async Task ConcurrentSaveDoesNotSendASecondCommand()
    {
        MemorySettingsDraft draft = CreateDraft();
        draft.Edit(draft.Current with { CooldownMinutes = 60 });
        TaskCompletionSource<MemoryOptimizerSettings> reply = new();
        int writes = 0;
        Task<MemoryOptimizerSettings> Write(MemoryOptimizerSettings value, CancellationToken token)
        {
            writes++;
            return reply.Task;
        }

        Task<bool> first = draft.SaveAsync(Write, CancellationToken.None);
        bool second = await draft.SaveWindowPreferencesAsync(true, true, Write, CancellationToken.None);
        Assert.IsFalse(second);
        Assert.AreEqual(1, writes);
        reply.SetResult(new() { CooldownMinutes = 60 });
        await first;
    }

    [TestMethod]
    public void RefreshPolicySkipsInactiveProcessesAndInvalidatesHistoryOnce()
    {
        MemoryRefreshPolicy policy = new();
        Assert.IsFalse(policy.ShouldRefreshProcesses);
        Assert.IsFalse(policy.NeedsHistory);
        policy.SelectPage("processes");
        Assert.IsTrue(policy.ShouldRefreshProcesses);
        policy.IsVisible = false;
        Assert.IsFalse(policy.ShouldRefreshProcesses);
        policy.IsVisible = true;
        policy.SelectPage("history");
        Assert.IsTrue(policy.NeedsHistory);
        Assert.IsTrue(policy.TryBeginHistory());
        Assert.IsFalse(policy.TryBeginHistory());
        policy.EndHistory(success: true);
        Assert.IsFalse(policy.NeedsHistory);
        policy.InvalidateHistory();
        Assert.IsTrue(policy.NeedsHistory);
    }

    [TestMethod]
    public void OperationGateRejectsDuplicatesAndServiceBusyState()
    {
        MemoryOperationGate gate = new();
        Assert.IsFalse(gate.TryBegin(serviceBusy: true));
        Assert.IsTrue(gate.TryBegin(serviceBusy: false));
        Assert.IsFalse(gate.TryBegin(serviceBusy: false));
        gate.End();
        Assert.IsTrue(gate.TryBegin(serviceBusy: false));
        gate.End();
    }

    [TestMethod]
    public void TrendSamplesHaveFiveSecondSpacingAndExpireAfterAMinute()
    {
        MemoryRamTrend trend = new();
        DateTimeOffset start = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < 20; index++)
        {
            Assert.IsTrue(trend.Add(Sample(start.AddSeconds(index * 5), 50)));
            Assert.IsFalse(trend.Add(Sample(start.AddSeconds(index * 5 + 1), 80)));
        }

        Assert.HasCount(13, trend.Samples);
        Assert.AreEqual(start.AddSeconds(35), trend.Samples[0].CapturedAtUtc);
        Assert.IsFalse(trend.Add(Sample(start.AddSeconds(90), 99)));
        Assert.IsFalse(trend.Add(Sample(start.AddSeconds(200), 50) with { TotalPhysicalBytes = 0 }));
    }

    [TestMethod]
    public void CommitDisplayDoesNotFallBackToTheProcessVirtualAddressSpace()
    {
        MemorySnapshot old = Sample(DateTimeOffset.UtcNow, 50);
        Assert.AreEqual("Brak danych", MemoryUiFormatting.FormatCommit(
            old, System.Globalization.CultureInfo.InvariantCulture));
        MemorySnapshot current = old with
        {
            CommitLimitBytes = 8UL * 1024 * 1024 * 1024,
            AvailableCommitBytes = 3UL * 1024 * 1024 * 1024,
        };
        Assert.AreEqual("5,0 GB / 8,0 GB",
            MemoryUiFormatting.FormatCommit(current, System.Globalization.CultureInfo.GetCultureInfo("pl-PL")));
    }

    private static MemorySettingsDraft CreateDraft()
    {
        MemorySettingsDraft draft = new();
        draft.Receive(new());
        return draft;
    }

    private static MemorySnapshot Sample(DateTimeOffset timestamp, uint load) =>
        new(timestamp, 1000, 500, 128_000_000_000_000, 127_000_000_000_000, load);
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dismode.Core.Cpu;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.History;
using Dismode.Core.Journal;
using Dismode.Core.Profiles;
using Dismode.Core.Sessions;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.Windows.Cpu;
using Dismode.Windows.Power;
using Dismode.Windows.Processes;
using Dismode.Windows.Profiles;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class GameSessionOrchestratorTests
{
    [TestMethod]
    public async Task ShutdownReadinessBlocksPreparedPlan()
    {
        string directory = CreateTestDirectory();
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                Path.Combine(directory, "unused.ready"));
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionShutdownReadiness idle =
                await orchestrator.GetShutdownReadinessAsync(
                    CancellationToken.None);
            _ = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            SessionShutdownReadiness prepared =
                await orchestrator.GetShutdownReadinessAsync(
                    CancellationToken.None);

            Assert.IsTrue(idle.CanShutdown);
            Assert.IsFalse(idle.HasPreparedPlan);
            Assert.IsFalse(prepared.CanShutdown);
            Assert.IsTrue(prepared.HasPreparedPlan);
            Assert.IsFalse(prepared.HasActiveSession);
        }
        finally
        {
            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ShutdownReservationBlocksNewPlan()
    {
        string directory = CreateTestDirectory();
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal = new(
            Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                Path.Combine(directory, "unused.ready"));
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionShutdownReadiness reservation =
                await orchestrator.ReserveShutdownAsync(
                    CancellationToken.None);

            Assert.IsTrue(reservation.CanShutdown);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => _ = await orchestrator.PrepareAsync(
                    profile.ProfileId,
                    CancellationToken.None));
        }
        finally
        {
            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ApprovedZeroChangePlanLaunchesTracksAndWritesHistory()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        ConstantFrameRateProvider frameRateProvider = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: frameRateProvider);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            Assert.IsFalse(plan.SystemMutationsEnabled);
            Assert.AreEqual(profile.ProfileId, plan.ProfileId);
            Assert.IsTrue(plan.Items.Any(item =>
                item.Code == "NO_SYSTEM_MUTATIONS"));

            GameSessionSnapshot active = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(OptimizationSessionState.Active, active.State);
            Assert.AreEqual(0, active.AppliedActionCount);
            Assert.IsTrue(IsProcessRunning(processId.Value));

            GameSessionSnapshot completed =
                await orchestrator.RestoreAsync(
                    plan.SessionId,
                    CancellationToken.None);
            GameSessionSnapshot? afterRestore =
                await orchestrator.GetActiveAsync(CancellationToken.None);
            IReadOnlyList<SessionSummary> history =
                await store.ListRecentAsync(10, CancellationToken.None);
            GameMetadata? metadata = await store.FindMetadataAsync(
                profile.ProfileId,
                CancellationToken.None);
            IReadOnlyList<RecoveryJournalEntry> records =
                await journal.ReadAllAsync(CancellationToken.None);

            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State);
            Assert.IsNull(afterRestore);
            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.Completed,
                history[0].Status);
            Assert.AreEqual(0, history[0].AppliedActionCount);
            Assert.IsNotNull(metadata);
            Assert.AreEqual("Manual", metadata.Source);
            Assert.AreEqual(history[0].EndedAtUtc, metadata.LastPlayedAtUtc);
            Assert.AreEqual(0L, metadata.TotalPlaytimeMinutes);
            Assert.IsNotNull(history[0].DurationSeconds);
            SessionFrameRateStatistics statistics =
                history[0].FrameRateStatistics
                ?? throw new AssertFailedException(
                    "Historia powinna zawierać prawdziwe próbki FPS.");
            Assert.IsGreaterThanOrEqualTo(
                1,
                statistics.SampleCount);
            Assert.AreEqual(
                120d,
                statistics.AverageFramesPerSecond,
                0.001d);
            Assert.AreEqual(
                1000d / 120d,
                statistics.AverageFrameTimeMilliseconds,
                0.001d);
            Assert.IsTrue(IsProcessRunning(processId.Value));
            Assert.IsTrue(records.Any(record =>
                record.SessionCheckpoint
                    == SessionCheckpoint.SnapshotComplete));
            Assert.IsTrue(records.Any(record =>
                record.SessionCheckpoint
                    == SessionCheckpoint.GameLaunched));
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint);
        }
        finally
        {
            if (processId is null && File.Exists(readyFile))
            {
                processId = ReadProcessId(readyFile);
            }

            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AttachOnlyStartRefusesToLaunchMissingGame()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "attach-only.ready");
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => _ = await orchestrator.StartAsync(
                    plan.PlanId,
                    plan.SessionId,
                    CancellationToken.None,
                    attachOnly: true));

            // Gdyby host mimo flagi uruchomil gre, harness zapisalby plik
            // gotowosci w ulamku sekundy. Czekamy dluzej niz on potrzebuje.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.IsFalse(
                File.Exists(readyFile),
                "Tryb „tylko dołącz” nie może uruchamiać gry.");

            GameSessionSnapshot? active =
                await orchestrator.GetActiveAsync(CancellationToken.None);
            SessionShutdownReadiness readiness =
                await orchestrator.GetShutdownReadinessAsync(
                    CancellationToken.None);
            IReadOnlyList<SessionSummary> history =
                await store.ListRecentAsync(10, CancellationToken.None);
            IReadOnlyList<RecoveryJournalEntry> records =
                await journal.ReadAllAsync(CancellationToken.None);

            Assert.IsNull(active);
            Assert.IsTrue(readiness.CanShutdown);
            Assert.IsFalse(readiness.HasPreparedPlan);
            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.FailedBeforeApply,
                history[0].Status);
            Assert.IsFalse(records.Any(record =>
                record.SessionCheckpoint == SessionCheckpoint.GameLaunched));
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint);
        }
        finally
        {
            if (File.Exists(readyFile))
            {
                await CloseProcessAsync(ReadProcessId(readyFile));
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task AttachOnlyStartAttachesToRunningGameWithoutDuplicate()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "attach-running.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            // Gra startuje poza Dismodeem, tak jak przy wykryciu z pulpitu.
            ProcessStartInfo startInfo = new()
            {
                FileName = profile.ExecutablePath,
                WorkingDirectory = profile.WorkingDirectory,
                UseShellExecute = false,
            };
            foreach (string argument in profile.LaunchArguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using (Process started = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The process test harness did not start."))
            {
                processId = started.Id;
            }

            await WaitForFileAsync(readyFile);
            Assert.AreEqual(processId, ReadProcessId(readyFile));
            DateTimeOffset readyWrittenAtUtc =
                File.GetLastWriteTimeUtc(readyFile);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            GameSessionSnapshot active = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                attachOnly: true);

            Assert.AreEqual(OptimizationSessionState.Active, active.State);
            Assert.StartsWith("Dołączono", active.Message);
            // Drugi egzemplarz nadpisalby plik gotowosci swoim PID.
            Assert.AreEqual(processId, ReadProcessId(readyFile));
            Assert.AreEqual(
                readyWrittenAtUtc,
                File.GetLastWriteTimeUtc(readyFile));

            GameSessionSnapshot completed = await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State);
            Assert.IsTrue(IsProcessRunning(processId.Value));
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task SessionLifecycleActivatesAndRestoresSystemProfile()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "system-profile.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal = new(
            Path.Combine(directory, "recovery.jsonl"));
        RecordingSystemProfileCoordinator systemProfile = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            systemProfileCoordinator: systemProfile);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            _ = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(profile.ProfileId, systemProfile.ActivatedProfileId);
            Assert.AreEqual(processId, systemProfile.ActivatedIdentity?.RuntimeKey.ProcessId);
            _ = await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
            Assert.AreEqual(profile.ProfileId, systemProfile.RestoredProfileId);
        }
        finally
        {
            if (processId is int id)
            {
                await CloseProcessAsync(id);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task DisabledFrameRateTrackingSkipsProviderUntilEnabled()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "tracking-disabled.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        CountingFrameRateProvider frameRateProvider = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: frameRateProvider);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            GameSessionSnapshot disabled = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.IsNull(disabled.FramesPerSecond);
            Assert.AreEqual(
                "Pomiar FPS jest wyłączony w ustawieniach Dismode.",
                disabled.FrameRateStatus);
            Assert.AreEqual(0, frameRateProvider.SampleCount);

            GameSessionSnapshot enabled =
                await orchestrator.SetFrameRateTrackingAsync(
                    plan.SessionId,
                    enabled: true,
                    CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.IsNull(enabled.FramesPerSecond);
            Assert.IsGreaterThanOrEqualTo(1, frameRateProvider.SampleCount);

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
        }
        finally
        {
            if (processId is null && File.Exists(readyFile))
            {
                processId = ReadProcessId(readyFile);
            }

            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ForceCloseTerminatesVerifiedGameAndFinalizesSession()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "force-close.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Force close harness",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    ["--ready-file", readyFile, "--ignore-close"],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            GameSessionSnapshot completed =
                await orchestrator.CloseGameAsync(
                    plan.SessionId,
                    forceTermination: true,
                    CancellationToken.None);
            await WaitForProcessExitAsync(processId.Value);
            processId = null;
            IReadOnlyList<RecoveryJournalEntry> records =
                await journal.ReadAllAsync(CancellationToken.None);
            IReadOnlyList<SessionSummary> history =
                await store.ListRecentAsync(10, CancellationToken.None);

            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State);
            Assert.IsNull(
                await orchestrator.GetActiveAsync(CancellationToken.None));
            Assert.HasCount(1, history);
            Assert.IsTrue(records.Any(record =>
                record.SessionCheckpoint
                    == SessionCheckpoint.GameCloseRequested));
            Assert.IsTrue(records.Any(record =>
                record.SessionCheckpoint
                    == SessionCheckpoint.GameForceTerminated));
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ManualExitKeepsRecoveryOpenUntilTelemetryCleanupSucceeds()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "cleanup-retry.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FailOnceStopFrameRateProvider frameRateProvider = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: frameRateProvider);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            await CloseProcessAsync(processId.Value);
            processId = null;
            GameSessionSnapshot recoveryRequired =
                await WaitForActiveStateAsync(
                    orchestrator,
                    OptimizationSessionState.RecoveryRequired);

            Assert.AreEqual(1, frameRateProvider.StopCount);
            Assert.AreEqual(1, recoveryRequired.ErrorCount);
            Assert.HasCount(
                0,
                await store.ListRecentAsync(10, CancellationToken.None));

            GameSessionSnapshot completed =
                await orchestrator.RestoreAsync(
                    plan.SessionId,
                    CancellationToken.None);

            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State);
            Assert.AreEqual(2, frameRateProvider.StopCount);
            Assert.IsNull(
                await orchestrator.GetActiveAsync(CancellationToken.None));
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task HistoryFailureDuringCompletionLeavesSessionRecoverable()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "history-fail.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FailOnceHistoryRepository history = new(store);
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            history,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50));

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            _ = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            await CloseProcessAsync(processId.Value);
            processId = null;
            GameSessionSnapshot recoveryRequired =
                await WaitForActiveStateAsync(
                    orchestrator,
                    OptimizationSessionState.RecoveryRequired);

            Assert.IsGreaterThan(0, recoveryRequired.ErrorCount);
            Assert.IsFalse(
                (await journal.ReadAllAsync(CancellationToken.None)).Any(
                    entry => entry.SessionCheckpoint
                        == SessionCheckpoint.ReconciliationComplete));

            GameSessionSnapshot completed =
                await orchestrator.RestoreAsync(
                    plan.SessionId,
                    CancellationToken.None);

            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State);
            Assert.IsNull(
                await orchestrator.GetActiveAsync(CancellationToken.None));
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task FailedStartRestoresSystemProfileAndKeepsJournalOpenOnRestoreFailure(
        bool restoreSucceeds)
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "start-fail.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal innerJournal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FailingCheckpointJournal journal = new(
            innerJournal,
            SessionCheckpoint.SessionActivated);
        RecordingSystemProfileCoordinator systemProfile =
            new() { RestoreSucceeds = restoreSucceeds };
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            systemProfileCoordinator: systemProfile);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            _ = await Assert.ThrowsExactlyAsync<IOException>(
                async () => await orchestrator.StartAsync(
                    plan.PlanId,
                    plan.SessionId,
                    CancellationToken.None,
                    enableFrameRateTracking: false));
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(profile.ProfileId, systemProfile.RestoredProfileId);
            bool finalized =
                (await innerJournal.ReadAllAsync(CancellationToken.None)).Any(
                    entry => entry.SessionCheckpoint
                        == SessionCheckpoint.ReconciliationComplete);
            Assert.AreEqual(restoreSucceeds, finalized);
        }
        finally
        {
            if (processId is int id)
            {
                await CloseProcessAsync(id);
            }

            await orchestrator.DisposeAsync();
            innerJournal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task RestartedHostRecoversLiveSessionThenFinalizesOnGameExit()
    {
        string directory = CreateTestDirectory();
        string databasePath = Path.Combine(directory, "user.db");
        string journalPath = Path.Combine(directory, "recovery.jsonl");
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore? firstStore = new(databasePath);
        AppendOnlyRecoveryJournal? firstJournal = new(journalPath);
        LocalGameSessionOrchestrator? firstHost = new(
            firstStore,
            firstStore,
            firstJournal,
            monitorInterval: TimeSpan.FromMilliseconds(50));
        SqliteUserDataStore? restartedStore = null;
        AppendOnlyRecoveryJournal? restartedJournal = null;
        LocalGameSessionOrchestrator? restartedHost = null;

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await firstStore.UpsertAsync(profile, CancellationToken.None);
            await firstHost.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await firstHost.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await firstHost.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);
            IReadOnlyList<RecoveryJournalEntry> beforeRestart =
                await firstJournal.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(
                SessionCheckpoint.SessionActivated,
                beforeRestart[^1].SessionCheckpoint);
            Assert.IsTrue(IsProcessRunning(processId.Value));
            PersistedSessionMetadata? persisted =
                JsonSerializer.Deserialize<PersistedSessionMetadata>(
                    beforeRestart[^1].Details!);
            Assert.IsNotNull(persisted);
            Assert.IsNotNull(persisted.RootProcess);
            ProcessIdentity? beforeRestartIdentity =
                await new ProcessIdentityProvider().TryCaptureAsync(
                    processId.Value,
                    CancellationToken.None);
            Assert.IsNotNull(beforeRestartIdentity);
            Assert.IsTrue(
                persisted.RootProcess.MatchesExecutable(
                    beforeRestartIdentity),
                $"persisted={persisted.RootProcess}; "
                + $"current={beforeRestartIdentity}");

            await firstHost.DisposeAsync();
            firstHost = null;
            firstJournal.Dispose();
            firstJournal = null;
            firstStore.Dispose();
            firstStore = null;

            restartedStore = new(databasePath);
            restartedJournal = new(journalPath);
            restartedHost = new(
                restartedStore,
                restartedStore,
                restartedJournal,
                monitorInterval: TimeSpan.FromMilliseconds(50));
            await restartedHost.InitializeAsync(CancellationToken.None);

            GameSessionSnapshot? recovered =
                await restartedHost.GetActiveAsync(CancellationToken.None);
            IReadOnlyList<RecoveryJournalEntry> afterRestart =
                await restartedJournal.ReadAllAsync(CancellationToken.None);
            ProcessIdentity? currentIdentity =
                await new ProcessIdentityProvider().TryCaptureAsync(
                    processId.Value,
                    CancellationToken.None);
            Assert.IsNotNull(
                recovered,
                "Recovery checkpoints: "
                + string.Join(
                    ", ",
                    afterRestart.Select(record =>
                        record.SessionCheckpoint?.ToString()
                        ?? record.EventKind.ToString()))
                + $"; process running: {IsProcessRunning(processId.Value)}"
                + $"; metadata: {beforeRestart[^1].Details}"
                + $"; current: {currentIdentity}");
            Assert.AreEqual(plan.SessionId, recovered.SessionId);
            Assert.AreEqual(OptimizationSessionState.Active, recovered.State);

            await CloseProcessAsync(processId.Value);
            processId = null;
            await WaitForNoActiveSessionAsync(restartedHost);
            IReadOnlyList<SessionSummary> history =
                await restartedStore.ListRecentAsync(
                    maximumCount: 10,
                    CancellationToken.None);

            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.RecoveredAfterCrash,
                history[0].Status);
            Assert.AreEqual(plan.SessionId, history[0].SessionId);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            if (firstHost is not null)
            {
                await firstHost.DisposeAsync();
            }

            if (restartedHost is not null)
            {
                await restartedHost.DisposeAsync();
            }

            firstJournal?.Dispose();
            restartedJournal?.Dispose();
            firstStore?.Dispose();
            restartedStore?.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task LauncherExitKeepsChildActiveAcrossHostRestart()
    {
        string directory = CreateTestDirectory();
        string databasePath = Path.Combine(directory, "user.db");
        string journalPath = Path.Combine(directory, "recovery.jsonl");
        string launcherReadyFile =
            Path.Combine(directory, "launcher.ready");
        string childReadyFile = Path.Combine(directory, "game-child.ready");
        int? launcherProcessId = null;
        int? childProcessId = null;
        SqliteUserDataStore? firstStore = new(databasePath);
        AppendOnlyRecoveryJournal? firstJournal = new(journalPath);
        LocalGameSessionOrchestrator? firstHost = new(
            firstStore,
            firstStore,
            firstJournal,
            monitorInterval: TimeSpan.FromMilliseconds(30));
        SqliteUserDataStore? restartedStore = null;
        AppendOnlyRecoveryJournal? restartedJournal = null;
        LocalGameSessionOrchestrator? restartedHost = null;

        try
        {
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Launcher tree harness",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    [
                        "--ready-file",
                        launcherReadyFile,
                        "--spawn-child-ready-file",
                        childReadyFile,
                        "--exit-after-spawn-ms",
                        "600",
                    ],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await firstStore.UpsertAsync(profile, CancellationToken.None);
            await firstHost.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await firstHost.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await firstHost.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);

            await WaitForFileAsync(launcherReadyFile);
            await WaitForFileAsync(childReadyFile);
            launcherProcessId = ReadProcessId(launcherReadyFile);
            childProcessId = ReadProcessId(childReadyFile);
            await WaitForProcessExitAsync(launcherProcessId.Value);
            launcherProcessId = null;
            RecoveryJournalEntry treeCheckpoint =
                await WaitForCheckpointAsync(
                    firstJournal,
                    SessionCheckpoint.GameProcessTreeObserved);
            PersistedSessionMetadata? persisted =
                JsonSerializer.Deserialize<PersistedSessionMetadata>(
                    treeCheckpoint.Details!);

            Assert.IsNotNull(persisted);
            Assert.IsNotNull(persisted.TrackedGameProcesses);
            Assert.IsTrue(persisted.TrackedGameProcesses.Any(identity =>
                identity.RuntimeKey.ProcessId == childProcessId.Value));
            Assert.IsTrue(IsProcessRunning(childProcessId.Value));
            GameSessionSnapshot? stillActive =
                await firstHost.GetActiveAsync(CancellationToken.None);
            Assert.IsNotNull(stillActive);
            Assert.AreEqual(
                OptimizationSessionState.Active,
                stillActive.State);

            await firstHost.DisposeAsync();
            firstHost = null;
            firstJournal.Dispose();
            firstJournal = null;
            firstStore.Dispose();
            firstStore = null;

            restartedStore = new(databasePath);
            restartedJournal = new(journalPath);
            restartedHost = new(
                restartedStore,
                restartedStore,
                restartedJournal,
                monitorInterval: TimeSpan.FromMilliseconds(30));
            await restartedHost.InitializeAsync(CancellationToken.None);

            GameSessionSnapshot? recovered =
                await restartedHost.GetActiveAsync(CancellationToken.None);
            Assert.IsNotNull(recovered);
            Assert.AreEqual(plan.SessionId, recovered.SessionId);
            Assert.AreEqual(
                OptimizationSessionState.Active,
                recovered.State);

            await restartedHost.CloseGameAsync(
                plan.SessionId,
                CancellationToken.None);
            await WaitForNoActiveSessionAsync(restartedHost);
            await WaitForProcessExitAsync(childProcessId.Value);
            childProcessId = null;
            IReadOnlyList<SessionSummary> history =
                await restartedStore.ListRecentAsync(
                    maximumCount: 10,
                    CancellationToken.None);

            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.RecoveredAfterCrash,
                history[0].Status);
        }
        finally
        {
            if (launcherProcessId is not null)
            {
                await CloseProcessAsync(launcherProcessId.Value);
            }

            if (childProcessId is not null)
            {
                await CloseProcessAsync(childProcessId.Value);
            }

            if (firstHost is not null)
            {
                await firstHost.DisposeAsync();
            }

            if (restartedHost is not null)
            {
                await restartedHost.DisposeAsync();
            }

            firstJournal?.Dispose();
            restartedJournal?.Dispose();
            firstStore?.Dispose();
            restartedStore?.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ActiveMonitorUsesLightweightRuntimeIdentityChecks()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        CountingIdentityProvider identityProvider = new();
        ManualGameProfileLauncher launcher = new(identityProvider);
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            launcher,
            identityProvider,
            monitorInterval: TimeSpan.FromMilliseconds(30));

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);
            int fullCapturesAfterLaunch =
                identityProvider.FullCaptureCount;

            await Task.Delay(TimeSpan.FromMilliseconds(250));

            Assert.AreEqual(
                fullCapturesAfterLaunch,
                identityProvider.FullCaptureCount,
                "Active monitoring must not hash the executable repeatedly.");
            Assert.IsGreaterThan(
                0,
                identityProvider.RuntimeMatchCount);

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
        }
        finally
        {
            if (processId is null && File.Exists(readyFile))
            {
                processId = ReadProcessId(readyFile);
            }

            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    [TestCategory("Live")]
    [Timeout(180_000)]
    public async Task RunningSessionActuallyConfinesABusyBackgroundProcess()
    {
        // Kazde ogniwo bylo zmierzone osobno: polityka daje maske, aktuator ja
        // naklada i zdejmuje, przelacznik powoluje petle, a maska poprawia p99
        // czasu klatki o 26%. Brakowalo dowodu, ze dzialaja razem — a wlasnie
        // tam raz juz przepadla cala funkcja, bo poprawka polityki zgubila sie
        // miedzy sesjami i nikt tego nie zauwazyl.
        //
        // Rola gry przypada harnessowi, wiec test nie potrzebuje ani gry,
        // ani PresentMon. Sprawdza jedno: czy w prawdziwej sesji, przy
        // domyslnych progach, proces liczacy bez przerwy dostaje maske i czy
        // ja oddaje po zakonczeniu.
        CpuAffinityDecision expected = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        if (!expected.ShouldApply)
        {
            Assert.Inconclusive(
                "Ta maszyna nie kwalifikuje sie do maski tla: "
                    + expected.Explanation);
            return;
        }

        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? gameProcessId = null;
        List<Process> hogs = [];
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(100),
            frameRateProvider: new ConstantFrameRateProvider(),
            enableProBalance: true,
            // Prawdziwy aktuator widzi przez nadzorce wylacznie nasze hogi.
            // Z pelnym probnikiem ograniczylby najciezsze procesy maszyny
            // deweloperskiej — edytor, serwer budowania, cudzy pomiar — a
            // maska i priorytet wejscia-wyjscia dziedzicza sie na wszystko,
            // co te procesy potem uruchomia.
            cpuProcessSourceFactory: () =>
                new Cpu.FilteredCpuProcessSource(
                    () => hogs.Select(hog => hog.Id)));

        try
        {
            ManualGameProfile profile =
                await CreateHarnessProfileAsync(readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            gameProcessId = ReadProcessId(readyFile);

            // Dwa procesy liczace bez przerwy: powyzej progu pojedynczego
            // procesu i powyzej progu tla.
            for (int index = 0; index < 2; index++)
            {
                hogs.Add(StartCpuHog());
            }

            nint before = hogs[0].ProcessorAffinity;
            Assert.AreNotEqual(
                (nint)(long)expected.Mask,
                before,
                "Proces juz ma maske tla; test nic by nie pokazal.");

            bool confined = await WaitUntilAsync(
                () => hogs.Any(hog =>
                {
                    hog.Refresh();
                    return !hog.HasExited
                        && (ulong)hog.ProcessorAffinity.ToInt64()
                            == expected.Mask;
                }),
                TimeSpan.FromSeconds(60));

            Assert.IsTrue(
                confined,
                "Zaden proces liczacy nie dostal maski tla w ciagu minuty. "
                    + "Sesja dziala, wiec albo bramka nie przepuscila, albo "
                    + "aktuator nie nalozyl maski — w obu przypadkach zysk "
                    + "zmierzony na czasach klatek nie trafi do gracza.");

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);

            bool restored = await WaitUntilAsync(
                () => hogs.All(hog =>
                {
                    hog.Refresh();
                    return hog.HasExited
                        || (ulong)hog.ProcessorAffinity.ToInt64()
                            != expected.Mask;
                }),
                TimeSpan.FromSeconds(30));
            Assert.IsTrue(
                restored,
                "Maska nie wrocila po zakonczeniu sesji. Proces zostalby "
                    + "zamkniety w cwiartce maszyny na stale.");
        }
        finally
        {
            foreach (Process hog in hogs)
            {
                try
                {
                    if (!hog.HasExited)
                    {
                        hog.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                {
                }

                hog.Dispose();
            }

            if (gameProcessId is not null)
            {
                await CloseProcessAsync(gameProcessId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    private static Process StartCpuHog()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$end = (Get-Date).AddMinutes(3); "
                + "while ((Get-Date) -lt $end) { $null = 1 }");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu obciazajacego.");
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return condition();
    }

    [TestMethod]
    [DataRow(true, DisplayName = "przelacznik wlaczony")]
    [DataRow(false, DisplayName = "przelacznik wylaczony")]
    public async Task ProBalanceToggleReachesTheCpuModule(bool enabled)
    {
        // Przelacznik w interfejsie nie znaczy nic, dopoki nie konczy sie
        // powolaniem petli ograniczania z prawdziwymi wspolpracownikami.
        // Sama logika petli ma wlasne testy sterujace TickAsync bezposrednio —
        // tutaj chodzi wylacznie o to, czy sesja ja w ogole uruchamia, i czy
        // przy wylaczonym przelaczniku nie siega po procesy, ktorych nikt nie
        // wskazal.
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        RecordingCpuProcessSource processes = new();
        RecordingProBalanceActuator actuator = new();
        int sourceFactoryCalls = 0;
        int actuatorFactoryCalls = 0;
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new ConstantFrameRateProvider(),
            enableProBalance: enabled,
            cpuProcessSourceFactory: () =>
            {
                Interlocked.Increment(ref sourceFactoryCalls);
                return processes;
            },
            proBalanceActuatorFactory: _ =>
            {
                Interlocked.Increment(ref actuatorFactoryCalls);
                return actuator;
            });

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            int expected = enabled ? 1 : 0;
            Assert.AreEqual(
                expected,
                sourceFactoryCalls,
                "Zrodlo probek procesow ma powstac dokladnie wtedy, gdy "
                    + "przelacznik jest wlaczony.");
            Assert.AreEqual(
                expected,
                actuatorFactoryCalls,
                "To samo dotyczy tego, co faktycznie zmienia priorytety.");

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);

            // Po zamknieciu sesji petla nie ma prawa dalej chodzic. Gdyby
            // chodzila, zostawialaby obce procesy ograniczone bez zadnej sesji,
            // ktora by je zwolnila.
            int after = processes.CaptureCount;
            await Task.Delay(TimeSpan.FromMilliseconds(400));
            Assert.AreEqual(
                after,
                processes.CaptureCount,
                "Po zakonczeniu sesji petla ograniczania ma stac.");
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, false)]
    public void GamePinningNeedsRestraintAndThePerGamePreference(
        bool restraint,
        bool pinPreference,
        bool expected) =>
        Assert.AreEqual(
            expected,
            LocalGameSessionOrchestrator.ShouldPinGame(
                restraint,
                pinPreference));

    [TestMethod]
    [DataRow(true, DisplayName = "ProBalance wlaczony")]
    [DataRow(false, DisplayName = "ProBalance wylaczony")]
    public async Task PinningOffLeavesTheGameProcessUntouched(bool proBalance)
    {
        // Przy wylaczonym przypinaniu zadnego zapisu do procesu gry: ani
        // zbiorow procesorow, ani maski. ProBalance (zrodlo probek tla) moze
        // dzialac dalej niezaleznie.
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        int sourceFactoryCalls = 0;
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new ConstantFrameRateProvider(),
            enableProBalance: proBalance,
            cpuProcessSourceFactory: () =>
            {
                Interlocked.Increment(ref sourceFactoryCalls);
                return new RecordingCpuProcessSource();
            },
            proBalanceActuatorFactory: _ => new RecordingProBalanceActuator());

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await store.SaveOptimizationPreferencesAsync(
                new GameOptimizationPreferences(
                    profile.ProfileId,
                    SavedGamePriorityMode.Normal,
                    backgroundRules: null,
                    DateTimeOffset.UtcNow,
                    pinGameToPreferredCores: false),
                CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(proBalance ? 1 : 0, sourceFactoryCalls);
            IReadOnlyList<uint>? cpuSets = ProcessCpuSets.TryRead(processId.Value);
            Assert.IsNotNull(cpuSets);
            Assert.IsEmpty(cpuSets, "Gra nie ma dostac zbiorow procesorow.");
            using Process game = Process.GetProcessById(processId.Value);
            Assert.AreEqual(
                Process.GetCurrentProcess().ProcessorAffinity,
                game.ProcessorAffinity,
                "Gra nie ma dostac maski affinity.");

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    [DataRow(true, true, DisplayName = "przelacznik wlaczony")]
    [DataRow(false, false, DisplayName = "przelacznik wylaczony")]
    public async Task KeepAwakeIsHeldOnlyDuringTheSessionAndOnlyWhenEnabled(
        bool keepAwake,
        bool expectedAcquired)
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FakeInhibitor inhibitor = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new ConstantFrameRateProvider(),
            optimizationPreferences: store,
            keepAwakeFactory: () => inhibitor);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await store.SaveOptimizationPreferencesAsync(
                new GameOptimizationPreferences(
                    profile.ProfileId,
                    SavedGamePriorityMode.Normal,
                    backgroundRules: null,
                    DateTimeOffset.UtcNow,
                    keepAwakeDuringSession: keepAwake),
                CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            Assert.AreEqual(0, inhibitor.AcquireCalls, "Plan niczego nie bierze.");

            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(expectedAcquired ? 1 : 0, inhibitor.AcquireCalls);
            Assert.AreEqual(expectedAcquired, inhibitor.Held);

            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
            Assert.IsFalse(inhibitor.Held, "Po sesji zadanie ma byc zdjete.");
            Assert.AreEqual(expectedAcquired ? 1 : 0, inhibitor.AcquireCalls);

            await orchestrator.DisposeAsync();
            Assert.IsFalse(inhibitor.Held);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task RefusedKeepAwakeDoesNotStopTheSessionAndIsReleased()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FakeInhibitor inhibitor = new() { Refuse = true };
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new ConstantFrameRateProvider(),
            keepAwakeFactory: () => inhibitor);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);

            Assert.AreEqual(1, inhibitor.AcquireCalls);
            Assert.IsFalse(inhibitor.Held);
            Assert.IsTrue(inhibitor.DisposeCalls >= 1);
            await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task KeepAwakeIsReleasedWhenStartFailsWithAnUnlistedException()
    {
        // Wyjatek spoza listy obslugiwanej w StartAsync (tu z probnika
        // klatek, po wzieciu zadania zasilania) nie moze zostawic zadania.
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "game.ready");
        int? processId = null;
        SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        FakeInhibitor inhibitor = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new ThrowingFrameRateProvider(),
            keepAwakeFactory: () => inhibitor);

        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(
                readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);

            _ = await Assert.ThrowsExactlyAsync<NotSupportedException>(
                async () => await orchestrator.StartAsync(
                    plan.PlanId,
                    plan.SessionId,
                    CancellationToken.None));

            Assert.AreEqual(1, inhibitor.AcquireCalls);
            Assert.IsFalse(inhibitor.Held, "Zadanie zasilania nie moze przezyc nieudanego startu.");
            // Gra byla juz uruchomiona: poczekaj na plik gotowosci, zeby ja
            // zamknac przed skasowaniem katalogu.
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);
        }
        finally
        {
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    private sealed class ThrowingFrameRateProvider : IFrameRateProvider
    {
        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Blad spoza listy.");

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeInhibitor : IExecutionStateInhibitor
    {
        public bool Refuse { get; init; }

        public int AcquireCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool Held { get; private set; }

        public bool TryAcquire(string reason)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
            AcquireCalls++;
            Held = !Refuse;
            return !Refuse;
        }

        public void Release() => Held = false;

        public void Dispose()
        {
            DisposeCalls++;
            Release();
        }
    }

    private sealed class RecordingCpuProcessSource : ICpuProcessSource
    {
        private int _captures;

        public int CaptureCount => Volatile.Read(ref _captures);

        public IReadOnlyList<CpuProcessSample> Capture()
        {
            Interlocked.Increment(ref _captures);
            return [];
        }
    }

    private sealed class RecordingProBalanceActuator : IProBalanceActuator
    {
        public ValueTask<bool> RestrainAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<bool> ReleaseAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private static async ValueTask<ManualGameProfile>
        CreateHarnessProfileAsync(string readyFile) =>
        await new ManualGameProfileFactory().CreateAsync(
            "Session harness",
            ProcessHarnessFixture.FindHarnessExecutable(),
            ["--ready-file", readyFile],
            OptimizationPreset.Safe,
            CancellationToken.None);

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.GameSessionOrchestratorTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WaitForFileAsync(string path)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForNoActiveSessionAsync(
        LocalGameSessionOrchestrator orchestrator)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(90));
        while (await orchestrator.GetActiveAsync(timeout.Token) is not null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task<GameSessionSnapshot> WaitForActiveStateAsync(
        LocalGameSessionOrchestrator orchestrator,
        OptimizationSessionState expectedState)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(90));
        while (true)
        {
            GameSessionSnapshot? active =
                await orchestrator.GetActiveAsync(timeout.Token);
            if (active?.State == expectedState)
            {
                return active;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        while (IsProcessRunning(processId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static async ValueTask<RecoveryJournalEntry>
        WaitForCheckpointAsync(
            AppendOnlyRecoveryJournal journal,
            SessionCheckpoint checkpoint)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        while (true)
        {
            IReadOnlyList<RecoveryJournalEntry> records =
                await journal.ReadAllAsync(timeout.Token);
            RecoveryJournalEntry? match = records.LastOrDefault(record =>
                record.SessionCheckpoint == checkpoint);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }
    }

    private static int ReadProcessId(string readyFile)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using FileStream stream = new(readyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                string text = reader.ReadToEnd().Trim();
                if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
                {
                    return pid;
                }
            }
            catch (IOException) when (i < 49)
            {
                Thread.Sleep(20);
            }
        }

        using FileStream finalStream = new(readyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader finalReader = new(finalStream);
        return int.Parse(finalReader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task CloseProcessAsync(int processId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            _ = ProcessWindowHelper.RequestGracefulClose(process);
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record PersistedSessionMetadata(
        GameProfileId ProfileId,
        string GameDisplayName,
        DateTimeOffset StartedAtUtc,
        ProcessIdentity? RootProcess,
        IReadOnlyList<ProcessIdentity>? TrackedGameProcesses);

    private sealed class CountingIdentityProvider : IProcessIdentityProvider
    {
        private readonly ProcessIdentityProvider _inner = new();
        private int _fullCaptureCount;
        private int _runtimeMatchCount;

        internal int FullCaptureCount =>
            Volatile.Read(ref _fullCaptureCount);

        internal int RuntimeMatchCount =>
            Volatile.Read(ref _runtimeMatchCount);

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            int processId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fullCaptureCount);
            return _inner.TryCaptureAsync(processId, cancellationToken);
        }

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            Process process,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fullCaptureCount);
            return _inner.TryCaptureAsync(process, cancellationToken);
        }

        public ValueTask<bool> MatchesRuntimeIdentityAsync(
            ProcessIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runtimeMatchCount);
            return _inner.MatchesRuntimeIdentityAsync(
                expectedIdentity,
                cancellationToken);
        }
    }

    private sealed class RecordingSystemProfileCoordinator :
        ISystemGameProfileCoordinator
    {
        internal GameProfileId? ActivatedProfileId { get; private set; }

        internal ProcessIdentity? ActivatedIdentity { get; private set; }

        internal GameProfileId? RestoredProfileId { get; private set; }

        internal bool RestoreSucceeds { get; init; } = true;

        public ValueTask<SystemGameProfileOperationResult> ActivateAsync(
            GameProfileId profileId,
            ProcessIdentity gameIdentity,
            CancellationToken cancellationToken)
        {
            ActivatedProfileId = profileId;
            ActivatedIdentity = gameIdentity;
            return ValueTask.FromResult(
                SystemGameProfileOperationResult.Applied(
                    "controlled activation"));
        }

        public ValueTask<SystemGameProfileOperationResult> RestoreAsync(
            GameProfileId profileId,
            CancellationToken cancellationToken)
        {
            RestoredProfileId = profileId;
            return ValueTask.FromResult(
                RestoreSucceeds
                    ? SystemGameProfileOperationResult.Restored(
                        "controlled restore")
                    : SystemGameProfileOperationResult.Failed(
                        "controlled restore failure"));
        }
    }

    private sealed class FailOnceHistoryRepository : ISessionHistoryRepository
    {
        private readonly ISessionHistoryRepository _inner;
        private int _failed;

        internal FailOnceHistoryRepository(ISessionHistoryRepository inner)
        {
            _inner = inner;
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            _inner.InitializeAsync(cancellationToken);

        public ValueTask AddAsync(
            SessionSummary summary,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _failed, 1) == 0)
            {
                throw new IOException("Controlled history failure.");
            }

            return _inner.AddAsync(summary, cancellationToken);
        }

        public ValueTask<IReadOnlyList<SessionSummary>> ListRecentAsync(
            int maximumCount,
            CancellationToken cancellationToken) =>
            _inner.ListRecentAsync(maximumCount, cancellationToken);

        public ValueTask<int> DeleteEndedBeforeAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken) =>
            _inner.DeleteEndedBeforeAsync(cutoffUtc, cancellationToken);
    }

    private sealed class FailingCheckpointJournal : IRecoveryJournal
    {
        private readonly IRecoveryJournal _inner;
        private readonly SessionCheckpoint _failingCheckpoint;

        internal FailingCheckpointJournal(
            IRecoveryJournal inner,
            SessionCheckpoint failingCheckpoint)
        {
            _inner = inner;
            _failingCheckpoint = failingCheckpoint;
        }

        public ValueTask<RecoveryJournalEntry> AppendDurableAsync(
            RecoveryJournalDraft draft,
            CancellationToken cancellationToken) =>
            draft.SessionCheckpoint == _failingCheckpoint
                ? throw new IOException("Controlled journal failure.")
                : _inner.AppendDurableAsync(draft, cancellationToken);

        public ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllAsync(
            CancellationToken cancellationToken) =>
            _inner.ReadAllAsync(cancellationToken);
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(3, false)]
    [DataRow(4, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(3, true)]
    [DataRow(4, true)]
    public async Task FrameRateFailureDoesNotPreventGameExitAndBackgroundRestore(
        int failureKind,
        bool failOnStop)
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "capture-failure.ready");
        int? processId = null;
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        using SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        MonitorFailureFrameRateProvider provider = new(failureKind, failOnStop);
        HandoffClock clock = new();
        await using LocalGameSessionOrchestrator orchestrator = new(
            store, store, journal,
            timeProvider: clock,
            monitorInterval: TimeSpan.FromMilliseconds(30),
            frameRateProvider: provider);

        try
        {
            background.Process.PriorityClass = ProcessPriorityClass.Normal;
            ManualGameProfile profile = await CreateHarnessProfileAsync(readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                [new BackgroundApplicationSelection(
                    background.Process.Id,
                    new DateTimeOffset(background.Process.StartTime.ToUniversalTime()),
                    BackgroundProcessActionMode.LowerPriority)],
                CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId, plan.SessionId, CancellationToken.None);
            await WaitForFileAsync(readyFile);
            processId = ReadProcessId(readyFile);
            await provider.MonitorSampling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            background.Process.Refresh();
            Assert.AreEqual(ProcessPriorityClass.BelowNormal, background.Process.PriorityClass);

            if (failOnStop)
            {
                // Wylaczenie podczas probki kieruje monitor do StopAsync.
                await orchestrator.SetFrameRateTrackingAsync(
                    plan.SessionId, false, CancellationToken.None);
            }

            provider.ReleaseSample.TrySetResult();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            GameSessionSnapshot? active;
            do
            {
                await Task.Delay(30, timeout.Token);
                active = await orchestrator.GetActiveAsync(timeout.Token);
            }
            while (active?.FrameRateStatus != MonitorFailureFrameRateProvider.FailureMessage);

            await Task.Delay(150, timeout.Token);
            Assert.AreEqual(2, provider.SampleCount);
            Assert.AreEqual(failOnStop ? 2 : 0, provider.StopCount);
            Assert.AreEqual(
                MonitorFailureFrameRateProvider.FailureMessage,
                (await orchestrator.GetActiveAsync(timeout.Token))?.FrameRateStatus);
            await CloseProcessAsync(processId.Value);
            processId = null;

            await WaitForCheckpointAsync(journal, SessionCheckpoint.GameHandoffWaiting);
            clock.Advance(GameProcessHandoff.Window);
            if (failureKind == 0)
            {
                GameSessionSnapshot recovery = await WaitForActiveStateAsync(
                    orchestrator, OptimizationSessionState.RecoveryRequired);
                Assert.AreEqual(1, recovery.ErrorCount);
                Assert.AreEqual(recovery.AppliedActionCount, recovery.RestoredActionCount);
                Assert.AreEqual(
                    MonitorFailureFrameRateProvider.FailureMessage,
                    recovery.FrameRateStatus);
                Assert.HasCount(
                    0, await store.ListRecentAsync(10, CancellationToken.None));
                background.Process.Refresh();
                Assert.AreEqual(
                    ProcessPriorityClass.Normal, background.Process.PriorityClass);
                provider.CleanupFails = false;
                await orchestrator.RestoreAsync(plan.SessionId, CancellationToken.None);
            }
            await WaitForNoActiveSessionAsync(orchestrator);
            background.Process.Refresh();
            Assert.AreEqual(ProcessPriorityClass.Normal, background.Process.PriorityClass);
            IReadOnlyList<SessionSummary> history =
                await store.ListRecentAsync(10, CancellationToken.None);
            Assert.HasCount(1, history);
            Assert.AreEqual(SessionCompletionStatus.Completed, history[0].Status);
        }
        finally
        {
            provider.ReleaseSample.TrySetResult();
            if (processId is not null)
            {
                await CloseProcessAsync(processId.Value);
            }

            await orchestrator.DisposeAsync();
            await background.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task ReEnablingFrameRateTrackingAfterAFailureResumesSampling()
    {
        // Po awarii pomiaru monitor przestaje probkowac, zeby nie sprzatac
        // PresentMon w kazdym obrocie. Ale uzytkownik, ktory wylaczy i wlaczy
        // FPS, prosi wprost o nowa probe — bez tego nakladka czekalaby na gre
        // do konca sesji.
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "capture-reenable.ready");
        using SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        MonitorFailureFrameRateProvider provider = new(1, false);
        await using LocalGameSessionOrchestrator orchestrator = new(
            store, store, journal,
            monitorInterval: TimeSpan.FromMilliseconds(30),
            frameRateProvider: provider);
        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId, CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId, plan.SessionId, CancellationToken.None);
            await WaitForFileAsync(readyFile);
            await provider.MonitorSampling.Task.WaitAsync(TimeSpan.FromSeconds(10));
            provider.ReleaseSample.TrySetResult();

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while ((await orchestrator.GetActiveAsync(timeout.Token))?.FrameRateStatus
                != MonitorFailureFrameRateProvider.FailureMessage)
            {
                await Task.Delay(30, timeout.Token);
            }

            int samplesAfterFailure = provider.SampleCount;
            await orchestrator.SetFrameRateTrackingAsync(
                plan.SessionId, false, CancellationToken.None);
            await orchestrator.SetFrameRateTrackingAsync(
                plan.SessionId, true, CancellationToken.None);

            while (provider.SampleCount == samplesAfterFailure)
            {
                await Task.Delay(30, timeout.Token);
            }

            Assert.IsGreaterThan(samplesAfterFailure, provider.SampleCount);
        }
        finally
        {
            provider.ReleaseSample.TrySetResult();
            await orchestrator.DisposeAsync();
            if (File.Exists(readyFile))
            {
                await CloseProcessAsync(ReadProcessId(readyFile));
            }

            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task FrameRateSamplingPropagatesMonitorLifetimeCancellation()
    {
        string directory = CreateTestDirectory();
        string readyFile = Path.Combine(directory, "capture-cancel.ready");
        using SqliteUserDataStore store = new(Path.Combine(directory, "user.db"));
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        MonitorFailureFrameRateProvider provider = new(1, false);
        await using LocalGameSessionOrchestrator orchestrator = new(
            store, store, journal,
            monitorInterval: TimeSpan.FromMilliseconds(30),
            frameRateProvider: provider);
        try
        {
            ManualGameProfile profile = await CreateHarnessProfileAsync(readyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId, CancellationToken.None);
            await orchestrator.StartAsync(
                plan.PlanId, plan.SessionId, CancellationToken.None);
            await WaitForFileAsync(readyFile);
            await provider.MonitorSampling.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await orchestrator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.IsTrue(provider.MonitorToken.IsCancellationRequested);
            Assert.AreEqual(2, provider.SampleCount);
            Assert.AreEqual(0, provider.StopCount);
        }
        finally
        {
            await orchestrator.DisposeAsync();
            if (File.Exists(readyFile))
            {
                await CloseProcessAsync(ReadProcessId(readyFile));
            }

            journal.Dispose();
            store.Dispose();
            DeleteDirectory(directory);
        }
    }

    private sealed class HandoffClock : TimeProvider
    {
        private long _offset;
        public override long GetTimestamp() => base.GetTimestamp() + Interlocked.Read(ref _offset);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _offset,
            (long)(duration.TotalSeconds * TimestampFrequency));
    }

    private sealed class MonitorFailureFrameRateProvider(
        int failureKind,
        bool failOnStop) : IFrameRateProvider
    {
        internal const string FailureMessage = "Kontrolowana awaria pomiaru FPS.";
        private int _sampleCount;
        private int _stopCount;

        internal TaskCompletionSource MonitorSampling { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseSample { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken MonitorToken { get; private set; }
        internal int SampleCount => Volatile.Read(ref _sampleCount);
        internal int StopCount => Volatile.Read(ref _stopCount);
        internal bool CleanupFails { get; set; } = true;

        public async ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sampleCount) > 1)
            {
                MonitorToken = cancellationToken;
                MonitorSampling.TrySetResult();
                await ReleaseSample.Task.WaitAsync(cancellationToken);
                if (!failOnStop)
                {
                    throw CreateFailure();
                }
            }

            return FrameRateSample.WaitingForGame(processIds.FirstOrDefault());
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Interlocked.Increment(ref _stopCount);
            if ((failOnStop && count == 2)
                || (failureKind == 0 && CleanupFails && (!failOnStop || count > 2)))
            {
                throw CreateFailure();
            }

            return ValueTask.CompletedTask;
        }

        private Exception CreateFailure() => failureKind switch
        {
            0 => new PresentMonCaptureCleanupException(FailureMessage),
            1 => new IOException(FailureMessage),
            2 => new System.ComponentModel.Win32Exception(FailureMessage),
            3 => new InvalidOperationException(FailureMessage),
            4 => new TimeoutException(FailureMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ConstantFrameRateProvider : IFrameRateProvider, ISessionFrameRateStatisticsSource
    {
        private SessionFrameRateStatisticsAccumulator? _statistics;

        public void SetSessionStatistics(SessionFrameRateStatisticsAccumulator statistics) =>
            _statistics = statistics;

        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _statistics?.Add(1000d / 120d);
            return ValueTask.FromResult(
                new FrameRateSample(
                    FrameRateStatus.Measuring,
                    FramesPerSecond: 120d,
                    FrameTimeMilliseconds: 8.33d,
                    processIds.FirstOrDefault(),
                    "Stała prawdziwa próbka adaptera testowego."));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingFrameRateProvider : IFrameRateProvider
    {
        private int _sampleCount;

        internal int SampleCount => Volatile.Read(ref _sampleCount);

        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _sampleCount);
            return ValueTask.FromResult(
                new FrameRateSample(
                    FrameRateStatus.Measuring,
                    FramesPerSecond: 120d,
                    FrameTimeMilliseconds: 8.33d,
                    processIds.FirstOrDefault(),
                    "Kontrolowana próbka adaptera testowego."));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceStopFrameRateProvider : IFrameRateProvider
    {
        private int _stopCount;

        internal int StopCount => Volatile.Read(ref _stopCount);

        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FrameRateSample.WaitingForGame(
                    processIds.FirstOrDefault()));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _stopCount) == 1)
            {
                throw new PresentMonCaptureCleanupException(
                    "Kontrolowana awaria cleanup PresentMon.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

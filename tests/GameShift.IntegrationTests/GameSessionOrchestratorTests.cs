using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.History;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.Sessions;
using GameShift.Data.Journal;
using GameShift.Data.UserData;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests;

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
            Assert.IsGreaterThanOrEqualTo(
                1L,
                metadata.TotalPlaytimeMinutes);
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
                8.33d,
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
                "Pomiar FPS jest wyłączony w ustawieniach GameShift.",
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
            "GameShift.GameSessionOrchestratorTests",
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
            new(TimeSpan.FromSeconds(10));
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
            new(TimeSpan.FromSeconds(10));
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
                SystemGameProfileOperationResult.Restored(
                    "controlled restore"));
        }
    }

    private sealed class ConstantFrameRateProvider : IFrameRateProvider
    {
        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

using System.Diagnostics;
using System.Globalization;
using Dismode.Core.Journal;
using Dismode.Core.Profiles;
using Dismode.Core.Sessions;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.Windows.Processes;
using Dismode.Windows.Profiles;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class SavedRuleGameSessionTests
{
    [TestMethod]
    public async Task ManualGameExitAutomaticallyRestoresSavedProcessRule()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.SavedRuleGameSessionTests",
            Guid.NewGuid().ToString("N"));
        string gameReadyFile = Path.Combine(directory, "game.ready");
        Directory.CreateDirectory(directory);
        int? gameProcessId = null;
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        TrackingFrameRateProvider frameRateProvider = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: frameRateProvider,
            optimizationPreferences: store);

        try
        {
            background.Process.PriorityClass = ProcessPriorityClass.Normal;
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Saved-rule game",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    ["--ready-file", gameReadyFile],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);
            await store.SaveOptimizationPreferencesAsync(
                new(
                    profile.ProfileId,
                    SavedGamePriorityMode.High,
                    [
                        new(
                            background.ExecutablePath,
                            SavedBackgroundActionMode.LowerPriority),
                    ],
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                backgroundApplications: [],
                desiredGamePriority: null,
                CancellationToken.None,
                useSavedBackgroundRules: true);

            Assert.IsTrue(plan.Items.Any(item =>
                item.Code == "SAVED_RULES_RESOLVED"));
            Assert.IsTrue(plan.Items.Any(item =>
                item.Code == "LOWER_BACKGROUND_PRIORITY"));
            Assert.IsFalse(plan.Items.Any(item =>
                item.Code == "BOOST_GAME_PRIORITY"));
            StringAssert.Contains(
                plan.SafetyMessage,
                "zatwierdzone działania: 1");
            StringAssert.Contains(plan.SafetyMessage, "priorytet gry: normalny");

            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(gameReadyFile);
            gameProcessId = ReadProcessId(gameReadyFile);
            background.Process.Refresh();
            Assert.AreEqual(
                ProcessPriorityClass.BelowNormal,
                background.Process.PriorityClass);
            IReadOnlyList<RecoveryJournalEntry> launchRecords =
                await journal.ReadAllAsync(CancellationToken.None);
            SessionCheckpoint[] checkpoints = launchRecords
                .Where(record => record.SessionCheckpoint is not null)
                .Select(record => record.SessionCheckpoint!.Value)
                .ToArray();
            int launchedIndex = Array.IndexOf(
                checkpoints,
                SessionCheckpoint.GameLaunched);
            int processesAppliedIndex = Array.IndexOf(
                checkpoints,
                SessionCheckpoint.ProcessesApplied);
            Assert.IsTrue(
                launchedIndex >= 0
                && processesAppliedIndex > launchedIndex,
                "Gra musi zostać uruchomiona przed sekwencyjnymi akcjami tła.");

            await CloseProcessAsync(gameProcessId.Value);
            gameProcessId = null;
            await WaitForNoActiveSessionAsync(orchestrator);
            background.Process.Refresh();
            Assert.AreEqual(
                ProcessPriorityClass.Normal,
                background.Process.PriorityClass);
            Assert.IsGreaterThanOrEqualTo(
                1,
                frameRateProvider.StopCount);
            IReadOnlyList<RecoveryJournalEntry> completedRecords =
                await journal.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                completedRecords[^1].SessionCheckpoint);
        }
        finally
        {
            if (gameProcessId is null && File.Exists(gameReadyFile))
            {
                gameProcessId = ReadProcessId(gameReadyFile);
            }

            if (gameProcessId is not null)
            {
                await CloseProcessAsync(gameProcessId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            await background.DisposeAsync();
            await DeleteDirectorySafelyAsync(directory);
        }
    }

    [TestMethod]
    public async Task GlobalRuleAppliesToGameWithoutOwnRulesAndRestoresAfterExit()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.SavedRuleGameSessionTests",
            Guid.NewGuid().ToString("N"));
        string gameReadyFile = Path.Combine(directory, "game.ready");
        Directory.CreateDirectory(directory);
        int? gameProcessId = null;
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        TrackingFrameRateProvider frameRateProvider = new();
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: frameRateProvider,
            optimizationPreferences: store,
            globalBackgroundRules: store);

        try
        {
            background.Process.PriorityClass = ProcessPriorityClass.Normal;
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Global-rule game",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    ["--ready-file", gameReadyFile],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);
            // Zadnych regul tej gry: dziala wylacznie regula dla wszystkich.
            await store.SaveGlobalBackgroundRulesAsync(
                [
                    new(
                        background.ExecutablePath,
                        SavedBackgroundActionMode.LowerPriority),
                ],
                CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                backgroundApplications: [],
                desiredGamePriority: null,
                CancellationToken.None,
                useSavedBackgroundRules: true);
            Assert.IsTrue(
                plan.Items.Any(item =>
                    item.Code == "LOWER_BACKGROUND_PRIORITY"),
                "Regula globalna ma wejsc do planu gry bez wlasnych regul.");

            await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None);
            await WaitForFileAsync(gameReadyFile);
            gameProcessId = ReadProcessId(gameReadyFile);
            background.Process.Refresh();
            Assert.AreEqual(
                ProcessPriorityClass.BelowNormal,
                background.Process.PriorityClass);

            await CloseProcessAsync(gameProcessId.Value);
            gameProcessId = null;
            await WaitForNoActiveSessionAsync(orchestrator);
            background.Process.Refresh();
            Assert.AreEqual(
                ProcessPriorityClass.Normal,
                background.Process.PriorityClass);
        }
        finally
        {
            if (gameProcessId is null && File.Exists(gameReadyFile))
            {
                gameProcessId = ReadProcessId(gameReadyFile);
            }

            if (gameProcessId is not null)
            {
                await CloseProcessAsync(gameProcessId.Value);
            }

            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            await background.DisposeAsync();
            await DeleteDirectorySafelyAsync(directory);
        }
    }

    [TestMethod]
    public async Task PerGameIgnoreRuleOverridesGlobalRule()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.SavedRuleGameSessionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new TrackingFrameRateProvider(),
            optimizationPreferences: store,
            globalBackgroundRules: store);

        try
        {
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Ignore-override game",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    ["--ready-file", Path.Combine(directory, "unused.ready")],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);
            await store.SaveOptimizationPreferencesAsync(
                new(
                    profile.ProfileId,
                    SavedGamePriorityMode.Normal,
                    [
                        new(
                            background.ExecutablePath,
                            SavedBackgroundActionMode.Ignore),
                    ],
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            await store.SaveGlobalBackgroundRulesAsync(
                [
                    new(
                        background.ExecutablePath,
                        SavedBackgroundActionMode.LowerPriority),
                ],
                CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                backgroundApplications: [],
                desiredGamePriority: null,
                CancellationToken.None,
                useSavedBackgroundRules: true);

            Assert.IsFalse(
                plan.Items.Any(item =>
                    item.Code == "LOWER_BACKGROUND_PRIORITY"),
                "„Ignoruj” zapisane dla gry ma wylaczac regule globalna.");
        }
        finally
        {
            await orchestrator.DisposeAsync();
            journal.Dispose();
            store.Dispose();
            await background.DisposeAsync();
            await DeleteDirectorySafelyAsync(directory);
        }
    }

    private static async Task DeleteDirectorySafelyAsync(string directory)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
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

    private sealed class TrackingFrameRateProvider : IFrameRateProvider
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
            Interlocked.Increment(ref _stopCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

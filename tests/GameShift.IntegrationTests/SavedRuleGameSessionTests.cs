using System.Diagnostics;
using System.Globalization;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.Sessions;
using GameShift.Data.Journal;
using GameShift.Data.UserData;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class SavedRuleGameSessionTests
{
    [TestMethod]
    public async Task ManualGameExitAutomaticallyRestoresSavedProcessRule()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.SavedRuleGameSessionTests",
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

    private static int ReadProcessId(string readyFile) =>
        int.Parse(
            File.ReadAllText(readyFile),
            NumberStyles.None,
            CultureInfo.InvariantCulture);

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

    private sealed class RenamedHarnessFixture : IAsyncDisposable
    {
        private readonly string _readyFile;

        private RenamedHarnessFixture(
            Process process,
            string executablePath,
            string readyFile)
        {
            Process = process;
            ExecutablePath = executablePath;
            _readyFile = readyFile;
        }

        internal Process Process { get; }

        internal string ExecutablePath { get; }

        internal static async ValueTask<RenamedHarnessFixture> StartAsync(
            string testDirectory)
        {
            string sourceExecutable =
                ProcessHarnessFixture.FindHarnessExecutable();
            string sourceDirectory =
                Path.GetDirectoryName(sourceExecutable)
                ?? throw new InvalidOperationException(
                    "The harness directory is unavailable.");
            string targetDirectory =
                Path.Combine(testDirectory, "background");
            Directory.CreateDirectory(targetDirectory);
            foreach (string sourceFile
                         in Directory.EnumerateFiles(sourceDirectory))
            {
                File.Copy(
                    sourceFile,
                    Path.Combine(
                        targetDirectory,
                        Path.GetFileName(sourceFile)));
            }

            string executablePath =
                Path.Combine(targetDirectory, "BackgroundWorker.exe");
            File.Copy(sourceExecutable, executablePath);
            string readyFile =
                Path.Combine(testDirectory, "background.ready");
            ProcessStartInfo startInfo = new()
            {
                FileName = executablePath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--ready-file");
            startInfo.ArgumentList.Add(readyFile);
            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The renamed background harness did not start.");

            try
            {
                await WaitForFileAsync(readyFile);
                process.Refresh();
                return new(process, executablePath, readyFile);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                process.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    _ = ProcessWindowHelper.RequestGracefulClose(Process);
                    using CancellationTokenSource timeout =
                        new(TimeSpan.FromSeconds(3));
                    try
                    {
                        await Process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Process.Kill(entireProcessTree: true);
                        await Process.WaitForExitAsync();
                    }
                }
            }
            finally
            {
                Process.Dispose();
                if (File.Exists(_readyFile))
                {
                    File.Delete(_readyFile);
                }
            }
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

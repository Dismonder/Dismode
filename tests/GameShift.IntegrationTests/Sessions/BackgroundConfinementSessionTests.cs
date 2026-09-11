using System.Diagnostics;
using System.Globalization;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Processes;
using GameShift.Core.History;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.Sessions;
using GameShift.Data.Journal;
using GameShift.Data.UserData;
using GameShift.Windows.Cpu;
using GameShift.Windows.NativeInterop;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests.Sessions;

/// <summary>
/// The hard affinity mask on approved background applications, from the first
/// second of a session, through the real orchestrator against real processes.
/// <para>
/// This is the lever that moved frame times in measurement — p99 better by
/// 60,7% on Valheim under full contention, where lowering priority moved
/// nothing — and until now it only ever reached a process reactively, several
/// seconds after it started misbehaving and only above a load gate that
/// ordinary use never crosses. Approving an application in the plan is the
/// consent; these tests prove the consent turns into the mask, that the mask
/// comes off when the session ends, and that it comes off after a host crash
/// as well, because a mask outlives the process that applied it.
/// </para>
/// </summary>
[TestClass]
public sealed class BackgroundConfinementSessionTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task ApprovedBackgroundApplicationIsConfinedFromSessionStartAndReleased()
    {
        CpuAffinityDecision corner = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        if (!corner.ShouldApply)
        {
            Assert.Inconclusive(
                "Ta maszyna nie kwalifikuje sie do maski tla: "
                    + corner.Explanation);
            return;
        }

        string directory = CreateTestDirectory();
        string gameReadyFile = Path.Combine(directory, "game.ready");
        int? gameProcessId = null;
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
            frameRateProvider: new SilentFrameRateProvider());

        try
        {
            background.Process.PriorityClass = ProcessPriorityClass.Normal;
            nint originalAffinity = background.Process.ProcessorAffinity;
            Assert.AreNotEqual(
                (nint)(long)corner.Mask,
                originalAffinity,
                "Proces tla juz ma maske cwiartki; test nic by nie pokazal.");
            uint originalMemoryPriority =
                ProcessMemoryPriorityAction.Read(background.Process)
                    .MemoryPriority;
            uint originalIoPriority = ReadIoPriority(background.Process);
            Assert.AreEqual(
                ProcessNativeMethods.MemoryPriorityNormal,
                originalMemoryPriority,
                "Test zaklada domyslny priorytet pamieci na starcie.");
            Assert.AreEqual(
                IoPriorityNativeMethods.IoPriorityNormal,
                originalIoPriority,
                "Test zaklada domyslny priorytet wejscia-wyjscia na starcie.");

            ManualGameProfile profile = await CreateGameProfileAsync(
                gameReadyFile);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);

            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                [Select(background.Process)],
                CancellationToken.None);

            // Zgoda ma dotyczyc tego, co sie naprawde stanie: plan musi
            // mowic o przypieciu, zanim uzytkownik go zatwierdzi.
            SessionPlanItem limit = plan.Items.Single(item =>
                item.Code == "LIMIT_BACKGROUND_CPU");
            StringAssert.Contains(limit.Description, "przypnij");
            StringAssert.Contains(limit.Recovery, "przypisanie rdzeni");

            GameSessionSnapshot started = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(gameReadyFile);
            gameProcessId = ReadProcessId(gameReadyFile);

            background.Process.Refresh();
            Assert.AreEqual(
                corner.Mask,
                (ulong)background.Process.ProcessorAffinity.ToInt64(),
                "Zatwierdzona aplikacja tla nie dostala maski cwiartki od "
                    + "startu sesji. To jest jedyna dzwignia CPU, ktora "
                    + "w pomiarze ruszyla czas klatki.");
            Assert.AreEqual(
                ProcessPriorityClass.BelowNormal,
                background.Process.PriorityClass);
            Assert.AreEqual(
                ProcessNativeMethods.MemoryPriorityVeryLow,
                ProcessMemoryPriorityAction.Read(background.Process)
                    .MemoryPriority,
                "Priorytet pamieci tla nie zostal obnizony. Pod presja "
                    + "pamieci to strony gry wylatywalyby pierwsze.");
            Assert.AreEqual(
                IoPriorityNativeMethods.IoPriorityVeryLow,
                ReadIoPriority(background.Process),
                "Priorytet wejscia-wyjscia tla nie zostal obnizony.");
            // Priorytet, EcoQoS, maska, pamiec, dysk — piec akcji na jedna
            // aplikacje.
            Assert.IsGreaterThanOrEqualTo(5, started.AppliedActionCount);
            StringAssert.Contains(started.Message, "na rdzeniach tła: 1");

            GameSessionSnapshot completed = await orchestrator.RestoreAsync(
                plan.SessionId,
                CancellationToken.None);

            Assert.AreEqual(
                OptimizationSessionState.Completed,
                completed.State,
                completed.Message);
            Assert.AreEqual(0, completed.ErrorCount);
            Assert.AreEqual(0, completed.ConflictCount);
            background.Process.Refresh();
            Assert.AreEqual(
                originalAffinity,
                background.Process.ProcessorAffinity,
                "Maska nie wrocila po zakonczeniu sesji. Proces zostalby "
                    + "zamkniety w cwiartce maszyny na stale.");
            Assert.AreEqual(
                ProcessPriorityClass.Normal,
                background.Process.PriorityClass);
            Assert.AreEqual(
                originalMemoryPriority,
                ProcessMemoryPriorityAction.Read(background.Process)
                    .MemoryPriority,
                "Priorytet pamieci nie wrocil po zakonczeniu sesji.");
            Assert.AreEqual(
                originalIoPriority,
                ReadIoPriority(background.Process),
                "Priorytet wejscia-wyjscia nie wrocil po zakonczeniu sesji.");
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
    [Timeout(120_000)]
    public async Task RestartedHostReleasesConfinedBackgroundApplication()
    {
        // Maska powinowactwa przezywa smierc GameShifta. Jesli host padnie
        // w trakcie sesji, jedyne, co o niej wie, to dziennik i punkt
        // kontrolny sesji — i to z nich nastepny start ma ja zdjac. Tu
        // symulujemy awarie: host znika bez przywracania czegokolwiek,
        // gra konczy sie w miedzyczasie, nowy host startuje na tym samym
        // dzienniku.
        CpuAffinityDecision corner = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        if (!corner.ShouldApply)
        {
            Assert.Inconclusive(
                "Ta maszyna nie kwalifikuje sie do maski tla: "
                    + corner.Explanation);
            return;
        }

        string directory = CreateTestDirectory();
        string databasePath = Path.Combine(directory, "user.db");
        string journalPath = Path.Combine(directory, "recovery.jsonl");
        string gameReadyFile = Path.Combine(directory, "game.ready");
        int? gameProcessId = null;
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        SqliteUserDataStore? firstStore = new(databasePath);
        AppendOnlyRecoveryJournal? firstJournal = new(journalPath);
        LocalGameSessionOrchestrator? firstHost = new(
            firstStore,
            firstStore,
            firstJournal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new SilentFrameRateProvider());
        SqliteUserDataStore? restartedStore = null;
        AppendOnlyRecoveryJournal? restartedJournal = null;
        LocalGameSessionOrchestrator? restartedHost = null;

        try
        {
            background.Process.PriorityClass = ProcessPriorityClass.Normal;
            nint originalAffinity = background.Process.ProcessorAffinity;

            ManualGameProfile profile = await CreateGameProfileAsync(
                gameReadyFile);
            await firstStore.UpsertAsync(profile, CancellationToken.None);
            await firstHost.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await firstHost.PrepareAsync(
                profile.ProfileId,
                [Select(background.Process)],
                CancellationToken.None);
            await firstHost.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(gameReadyFile);
            gameProcessId = ReadProcessId(gameReadyFile);

            background.Process.Refresh();
            Assert.AreEqual(
                corner.Mask,
                (ulong)background.Process.ProcessorAffinity.ToInt64(),
                "Warunek wstepny: maska musi byc nalozona przed awaria.");

            // Awaria hosta. Zadnego Restore, zadnego zwalniania.
            await firstHost.DisposeAsync();
            firstHost = null;
            firstJournal.Dispose();
            firstJournal = null;
            firstStore.Dispose();
            firstStore = null;

            background.Process.Refresh();
            Assert.AreEqual(
                corner.Mask,
                (ulong)background.Process.ProcessorAffinity.ToInt64(),
                "Samo zniknięcie hosta nie zdejmuje maski — dokladnie "
                    + "dlatego odtwarzanie musi to zrobic.");

            await CloseProcessAsync(gameProcessId.Value);
            gameProcessId = null;

            restartedStore = new(databasePath);
            restartedJournal = new(journalPath);
            restartedHost = new(
                restartedStore,
                restartedStore,
                restartedJournal,
                monitorInterval: TimeSpan.FromMilliseconds(50),
                frameRateProvider: new SilentFrameRateProvider());
            await restartedHost.InitializeAsync(CancellationToken.None);

            Assert.IsNull(
                await restartedHost.GetActiveAsync(CancellationToken.None),
                "Gra juz nie dziala, wiec sesja ma zostac domknieta, "
                    + "a nie wznowiona.");
            background.Process.Refresh();
            Assert.AreEqual(
                originalAffinity,
                background.Process.ProcessorAffinity,
                "Po restarcie hosta maska zostala na procesie tla. Bez tego "
                    + "kazda awaria zostawia cudza aplikacje w cwiartce "
                    + "maszyny na stale.");
            Assert.AreEqual(
                ProcessPriorityClass.Normal,
                background.Process.PriorityClass);
            Assert.AreEqual(
                ProcessNativeMethods.MemoryPriorityNormal,
                ProcessMemoryPriorityAction.Read(background.Process)
                    .MemoryPriority,
                "Po restarcie hosta priorytet pamieci zostal obnizony.");
            Assert.AreEqual(
                IoPriorityNativeMethods.IoPriorityNormal,
                ReadIoPriority(background.Process),
                "Po restarcie hosta priorytet wejscia-wyjscia zostal "
                    + "obnizony.");

            IReadOnlyList<SessionSummary> history =
                await restartedStore.ListRecentAsync(
                    10,
                    CancellationToken.None);
            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.RecoveredAfterCrash,
                history[0].Status);
            IReadOnlyList<RecoveryJournalEntry> records =
                await restartedJournal.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint);
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
            await background.DisposeAsync();
            await DeleteDirectorySafelyAsync(directory);
        }
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task RestartedHostReleasesReactivelyRestrainedProcess()
    {
        // Ograniczenia z petli ProBalance nikt nie planuje z gory: proces
        // trafia do niej, bo zaczal zjadac procesor w trakcie gry. Dziennik
        // ma wpisy kazdej z akcji, ale po awarii hosta nie ma kto ich
        // dopasowac do procesu — od tego jest ksiega ograniczen i punkt
        // kontrolny sesji. Aktuator jest prawdziwy, ograniczenie jest
        // prawdziwe; symulowana jest tylko smierc hosta przed zwolnieniem.
        CpuAffinityDecision corner = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        if (!corner.ShouldApply)
        {
            Assert.Inconclusive(
                "Ta maszyna nie kwalifikuje sie do maski tla: "
                    + corner.Explanation);
            return;
        }

        string directory = CreateTestDirectory();
        string databasePath = Path.Combine(directory, "user.db");
        string journalPath = Path.Combine(directory, "recovery.jsonl");
        string gameReadyFile = Path.Combine(directory, "game.ready");
        int? gameProcessId = null;
        Process hog = StartCpuHog();
        SqliteUserDataStore? firstStore = new(databasePath);
        AppendOnlyRecoveryJournal? firstJournal = new(journalPath);
        AppendOnlyRecoveryJournal journalForActuator = firstJournal;
        LocalGameSessionOrchestrator? firstHost = new(
            firstStore,
            firstStore,
            firstJournal,
            monitorInterval: TimeSpan.FromMilliseconds(50),
            frameRateProvider: new SilentFrameRateProvider(),
            enableProBalance: true,
            proBalanceSettings: new ProBalanceSettings
            {
                // Bramki i histereza sa sprawdzane osobno; tu liczy sie
                // tylko to, zeby ograniczenie zaszlo szybko i nie puscilo
                // przed symulowana awaria.
                BackgroundLoadCores = 0,
                SustainedSamples = 1,
                MinimumRestraint = TimeSpan.FromMinutes(5),
                MaximumRestraint = TimeSpan.FromMinutes(10),
            },
            proBalanceActuatorFactory: sessionId => new CrashingActuator(
                new JournaledProBalanceActuator(
                    journalForActuator,
                    sessionId,
                    backgroundAffinityMask: corner.Mask)));
        SqliteUserDataStore? restartedStore = null;
        AppendOnlyRecoveryJournal? restartedJournal = null;
        LocalGameSessionOrchestrator? restartedHost = null;

        try
        {
            nint originalAffinity = hog.ProcessorAffinity;
            ProcessPriorityClass originalPriority = hog.PriorityClass;
            uint originalIoPriority = ReadIoPriority(hog);

            ManualGameProfile profile = await CreateGameProfileAsync(
                gameReadyFile);
            await firstStore.UpsertAsync(profile, CancellationToken.None);
            await firstHost.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await firstHost.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            await firstHost.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: false);
            await WaitForFileAsync(gameReadyFile);
            gameProcessId = ReadProcessId(gameReadyFile);

            bool restrained = await WaitUntilAsync(
                () =>
                {
                    hog.Refresh();
                    return (ulong)hog.ProcessorAffinity.ToInt64()
                        == corner.Mask;
                },
                TimeSpan.FromSeconds(45));
            Assert.IsTrue(
                restrained,
                "Petla ograniczania nie nalozyla maski na proces liczacy "
                    + "w ciagu 45 s.");

            // Ksiega musiala zapisac punkt kontrolny z tym ograniczeniem,
            // zanim host padnie — inaczej nastepny start nie ma czego czytac.
            IReadOnlyList<RecoveryJournalEntry> beforeCrash =
                await firstJournal.ReadAllAsync(CancellationToken.None);
            Assert.IsTrue(
                beforeCrash.Any(record =>
                    record.SessionCheckpoint
                        == SessionCheckpoint.BackgroundRestraintChanged),
                "Brak punktu kontrolnego BackgroundRestraintChanged: "
                    + "aktuator nie zameldowal ograniczenia do ksiegi.");

            // Awaria hosta: aktuator nie zwalnia niczego.
            await firstHost.DisposeAsync();
            firstHost = null;
            firstJournal.Dispose();
            firstJournal = null;
            firstStore.Dispose();
            firstStore = null;

            hog.Refresh();
            Assert.AreEqual(
                corner.Mask,
                (ulong)hog.ProcessorAffinity.ToInt64(),
                "Warunek symulacji: po awarii maska ma zostac na procesie.");

            await CloseProcessAsync(gameProcessId.Value);
            gameProcessId = null;

            restartedStore = new(databasePath);
            restartedJournal = new(journalPath);
            restartedHost = new(
                restartedStore,
                restartedStore,
                restartedJournal,
                monitorInterval: TimeSpan.FromMilliseconds(50),
                frameRateProvider: new SilentFrameRateProvider());
            await restartedHost.InitializeAsync(CancellationToken.None);

            Assert.IsNull(
                await restartedHost.GetActiveAsync(CancellationToken.None));
            hog.Refresh();
            Assert.AreEqual(
                originalAffinity,
                hog.ProcessorAffinity,
                "Po restarcie hosta maska z petli ograniczania zostala na "
                    + "procesie. To jest luka, ktora ksiega ograniczen ma "
                    + "zamykac.");
            Assert.AreEqual(originalPriority, hog.PriorityClass);
            Assert.AreEqual(originalIoPriority, ReadIoPriority(hog));

            IReadOnlyList<RecoveryJournalEntry> records =
                await restartedJournal.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint);
        }
        finally
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

            if (gameProcessId is null && File.Exists(gameReadyFile))
            {
                gameProcessId = ReadProcessId(gameReadyFile);
            }

            if (gameProcessId is not null)
            {
                await CloseProcessAsync(gameProcessId.Value);
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
            await DeleteDirectorySafelyAsync(directory);
        }
    }

    /// <summary>
    /// The real actuator, minus the release: what a host that died
    /// mid-session looks like to the processes it had restrained.
    /// </summary>
    private sealed class CrashingActuator(JournaledProBalanceActuator inner) :
        IProBalanceActuator,
        IRestraintLedgerAware
    {
        public void AttachLedger(IRestraintLedger ledger) =>
            inner.AttachLedger(ledger);

        public ValueTask<bool> RestrainAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            inner.RestrainAsync(runtimeKey, cancellationToken);

        public ValueTask<bool> ReleaseAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
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
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu obciazajacego.");

        // Pierwszy odczyt czasu procesora musi miec co odjac, a PowerShell
        // potrzebuje chwili, zanim zacznie faktycznie palic rdzen.
        Thread.Sleep(1500);
        process.Refresh();
        return process;
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

    private static uint ReadIoPriority(Process process)
    {
        int status = IoPriorityNativeMethods.NtQueryInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            out uint priority,
            sizeof(uint),
            out _);
        Assert.AreEqual(
            IoPriorityNativeMethods.StatusSuccess,
            status,
            $"NtQueryInformationProcess(ProcessIoPriority) zwrocil 0x{status:X8}.");
        return priority;
    }

    private static BackgroundApplicationSelection Select(Process process) =>
        new(
            process.Id,
            new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero),
            BackgroundProcessActionMode.LowerPriorityAndEcoQos);

    private static async ValueTask<ManualGameProfile> CreateGameProfileAsync(
        string readyFile) =>
        await new ManualGameProfileFactory().CreateAsync(
            "Confinement game",
            ProcessHarnessFixture.FindHarnessExecutable(),
            ["--ready-file", readyFile],
            OptimizationPreset.Safe,
            CancellationToken.None);

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.BackgroundConfinementSessionTests",
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

    private static int ReadProcessId(string readyFile)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using FileStream stream = new(
                    readyFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                string text = reader.ReadToEnd().Trim();
                if (int.TryParse(
                        text,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int pid))
                {
                    return pid;
                }
            }
            catch (IOException) when (attempt < 49)
            {
            }

            Thread.Sleep(20);
        }

        throw new InvalidOperationException(
            $"Plik gotowosci {readyFile} nie zawiera identyfikatora procesu.");
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

    private static async Task DeleteDirectorySafelyAsync(string directory)
    {
        for (int attempt = 0; attempt < 20; attempt++)
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
    }

    private sealed class SilentFrameRateProvider : IFrameRateProvider
    {
        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                FrameRateSample.WaitingForGame(processIds.FirstOrDefault()));
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

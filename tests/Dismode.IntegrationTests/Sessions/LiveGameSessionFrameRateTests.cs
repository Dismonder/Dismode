using System.Globalization;
using Dismode.Core.Profiles;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.Windows.Processes;
using Dismode.Windows.Profiles;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

/// <summary>
/// The frame rate a real game session reports, end to end: the same
/// orchestrator the session host runs, launching a real game and polling the
/// snapshot the overlay and the session history read.
/// <para>
/// Every recorded game session on the development machine ended with zero
/// frame samples, while PresentMon attached directly to a rendering process
/// measured it correctly. The gap is somewhere between the session and the
/// capture, and only a real game shows it. Opt-in: set
/// <c>DISMODE_LIVE_GAME_EXE</c> to the game's executable (and optionally
/// <c>DISMODE_LIVE_GAME_ARGS</c>). Without it the test is inconclusive.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveGameSessionFrameRateTests
{
    private static readonly TimeSpan PollDuration = TimeSpan.FromSeconds(120);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(600_000)]
    public async Task RealGameSessionReportsMeasuredFrameRate()
    {
        string? executable =
            Environment.GetEnvironmentVariable("DISMODE_LIVE_GAME_EXE");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            Assert.Inconclusive("Ustaw DISMODE_LIVE_GAME_EXE na plik gry.");
            return;
        }

        string presentMon = FindPresentMon();
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.LiveGameSessionFrameRateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previousLogPath = FrameCaptureLog.PathOverride;
        string logPath = Path.Combine(directory, "frame-capture.log");

        string[] arguments =
            (Environment.GetEnvironmentVariable("DISMODE_LIVE_GAME_ARGS")
                ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        using SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        await using LocalGameSessionOrchestrator orchestrator = new(
            store,
            store,
            journal,
            frameRateProvider: new PresentMonFrameRateProvider(presentMon));

        // Nakladka mierzy w tym samym czasie co sesja, jak w aplikacji: UI
        // ma wlasnego providera, SessionHost swojego. Wspolna sesja ETW
        // sprawiala, ze zaden z nich nie dostawal klatek.
        await using PresentMonFrameRateProvider overlay = new(
            presentMon,
            captureRole: PresentMonCaptureRole.Overlay);
        int measuring = 0;
        int overlayMeasuring = 0;
        Dismode.Core.Domain.Identifiers.SessionId? sessionId = null;
        FrameCaptureLog.PathOverride = logPath;
        try
        {
            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    Path.GetFileNameWithoutExtension(executable),
                    executable,
                    arguments,
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);
            await orchestrator.InitializeAsync(CancellationToken.None);
            SessionPlanPreview plan = await orchestrator.PrepareAsync(
                profile.ProfileId,
                CancellationToken.None);
            sessionId = plan.SessionId;
            _ = await orchestrator.StartAsync(
                plan.PlanId,
                plan.SessionId,
                CancellationToken.None,
                enableFrameRateTracking: true);

            DateTimeOffset end = DateTimeOffset.UtcNow + PollDuration;
            int second = 0;
            while (DateTimeOffset.UtcNow < end)
            {
                GameSessionSnapshot? snapshot =
                    await orchestrator.GetActiveAsync(CancellationToken.None);
                if (snapshot is null)
                {
                    TestContext.WriteLine($"t={second} brak aktywnej sesji");
                    break;
                }

                if (snapshot.FramesPerSecond is not null)
                {
                    measuring++;
                }

                FrameRateSample? overlaySample =
                    snapshot.FrameRateProcessId is int gamePid
                        ? await overlay.SampleAsync(
                            [gamePid],
                            CancellationToken.None)
                        : null;
                if (overlaySample?.FramesPerSecond is not null)
                {
                    overlayMeasuring++;
                }

                TestContext.WriteLine(
                    $"t={second} state={snapshot.State} "
                        + $"fps={snapshot.FramesPerSecond?.ToString("F1", CultureInfo.InvariantCulture)} "
                        + $"overlay={overlaySample?.FramesPerSecond?.ToString("F1", CultureInfo.InvariantCulture)} "
                        + $"status={snapshot.FrameRateStatus} "
                        + $"pid={snapshot.FrameRateProcessId}");
                second++;
                await Task.Delay(1000);
            }
        }
        finally
        {
            if (sessionId is { } id)
            {
                try
                {
                    _ = await orchestrator.CloseGameAsync(
                        id,
                        forceTermination: true,
                        CancellationToken.None);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException)
                {
                    TestContext.WriteLine(
                        "Zamkniecie gry: " + exception.Message);
                }
            }

            FrameCaptureLog.PathOverride = previousLogPath;
            if (File.Exists(logPath))
            {
                TestContext.WriteLine("---- frame-capture.log ----");
                TestContext.WriteLine(await File.ReadAllTextAsync(logPath));
            }
        }

        Assert.IsGreaterThanOrEqualTo(
            30,
            measuring,
            "Sesja prawdziwej gry nie pokazala zmierzonego FPS przez "
                + "wiekszosc pomiaru.");
        Assert.IsGreaterThanOrEqualTo(
            30,
            overlayMeasuring,
            "Nakladka mierzaca razem z sesja nie pokazala zmierzonego FPS.");
    }

    private static string FindPresentMon()
    {
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Dismode",
            "Tools",
            "PresentMon",
            PresentMonComponent.ExecutableFileName);
        if (File.Exists(installed))
        {
            return installed;
        }

        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            string candidate = Path.Combine(
                current.FullName,
                "third_party",
                "PresentMon",
                PresentMonComponent.ExecutableFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Inconclusive("Brak PresentMon.");
        return string.Empty;
    }
}

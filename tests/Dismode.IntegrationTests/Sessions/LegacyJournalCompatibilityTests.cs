using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.History;
using Dismode.Core.Journal;
using Dismode.Core.Profiles;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.Windows.Processes;
using Dismode.Windows.Profiles;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

/// <summary>
/// A journal written by the release before the background bundle existed:
/// checkpoints without the affinity mask, without restrained processes,
/// with background applications that carry only the priority and EcoQoS
/// ids. The user's machine has exactly such a journal on disk, and the
/// first start after the update reads it. It has to read, finish the
/// session, and touch nothing the old session did not touch.
/// </summary>
[TestClass]
public sealed class LegacyJournalCompatibilityTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task RecoversASessionRecordedBeforeTheBackgroundBundleExisted()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-stary-dziennik-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SqliteUserDataStore store =
            new(Path.Combine(directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(directory, "recovery.jsonl"));
        RenamedHarnessFixture background =
            await RenamedHarnessFixture.StartAsync(directory);
        LocalGameSessionOrchestrator? orchestrator = null;

        try
        {
            ProcessIdentityProvider identities = new();
            ProcessIdentity gameIdentity;
            await using (ProcessHarnessFixture game =
                await ProcessHarnessFixture.StartAsync())
            {
                gameIdentity = await identities.TryCaptureAsync(
                        game.Process,
                        CancellationToken.None)
                    ?? throw new AssertFailedException(
                        "Nie udalo sie odczytac tozsamosci gry.");
            }

            ProcessIdentity backgroundIdentity =
                await identities.TryCaptureAsync(
                    background.Process,
                    CancellationToken.None)
                ?? throw new AssertFailedException(
                    "Nie udalo sie odczytac tozsamosci aplikacji tla.");
            background.Process.PriorityClass = ProcessPriorityClass.Normal;

            ManualGameProfile profile =
                await new ManualGameProfileFactory().CreateAsync(
                    "Legacy game",
                    ProcessHarnessFixture.FindHarnessExecutable(),
                    [],
                    OptimizationPreset.Safe,
                    CancellationToken.None);
            await store.UpsertAsync(profile, CancellationToken.None);

            // Dokladnie te pola, ktore zapisywalo 0.6.6: bez maski, bez
            // ograniczen reaktywnych, aplikacja tla tylko z priorytetem
            // i EcoQoS (tryb 3). Ksztalt tozsamosci i identyfikatora profilu
            // pochodzi z tych samych opcji serializacji, ktorych uzywa
            // orkiestrator, wiec test sprawdza brak nowych pol, nie
            // przepisywanie znanych.
            JsonSerializerOptions options = new(JsonSerializerDefaults.General);
            SessionId sessionId = SessionId.Create();
            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
            JsonNode? gameNode = JsonSerializer.SerializeToNode(
                gameIdentity,
                options);
            var backgroundApplication = new
            {
                ActionId = Guid.NewGuid(),
                IdempotencyKey = Guid.NewGuid(),
                DisplayName = "BackgroundWorker",
                Identity = JsonSerializer.SerializeToNode(
                    backgroundIdentity,
                    options),
                ActionMode = 3,
                EcoQosActionId = Guid.NewGuid(),
                EcoQosIdempotencyKey = Guid.NewGuid(),
                WorkingDirectory = (string?)null,
                Arguments = (string[]?)null,
                EstimatedWorkingSetBytes = 12_345L,
            };
            string Metadata(bool launched) => JsonSerializer.Serialize(
                new
                {
                    ProfileId = JsonSerializer.SerializeToNode(
                        profile.ProfileId,
                        options),
                    GameDisplayName = profile.DisplayName,
                    StartedAtUtc = startedAtUtc,
                    RootProcess = launched ? gameNode : null,
                    TrackedGameProcesses = launched
                        ? new[] { gameNode }
                        : null,
                    BackgroundApplications = new[] { backgroundApplication },
                    GamePriority = (object?)null,
                    AppliedActionCount = 2,
                    FrameRateTrackingEnabled = false,
                    SystemProfileActive = false,
                },
                options);

            SessionCheckpointWriter writer = new(journal);
            await writer.RecordAsync(
                sessionId,
                SessionCheckpoint.SnapshotComplete,
                Metadata(launched: false),
                CancellationToken.None);
            await writer.RecordAsync(
                sessionId,
                SessionCheckpoint.GameLaunched,
                Metadata(launched: true),
                CancellationToken.None);
            await writer.RecordAsync(
                sessionId,
                SessionCheckpoint.ProcessesApplied,
                Metadata(launched: true),
                CancellationToken.None);
            await writer.RecordAsync(
                sessionId,
                SessionCheckpoint.SessionActivated,
                Metadata(launched: true),
                CancellationToken.None);

            orchestrator = new(
                store,
                store,
                journal,
                monitorInterval: TimeSpan.FromMilliseconds(50),
                frameRateProvider: new SilentFrameRateProvider());
            await orchestrator.InitializeAsync(CancellationToken.None);

            Assert.IsNull(
                await orchestrator.GetActiveAsync(CancellationToken.None),
                "Gra juz nie dziala, wiec sesja ma zostac domknieta.");
            IReadOnlyList<RecoveryJournalEntry> records =
                await journal.ReadAllAsync(CancellationToken.None);
            Assert.AreEqual(
                SessionCheckpoint.ReconciliationComplete,
                records[^1].SessionCheckpoint,
                "Stary dziennik ma sie domknac, nie zawiesic sesji w stanie "
                    + "wymagajacym odtwarzania.");
            IReadOnlyList<SessionSummary> history =
                await store.ListRecentAsync(10, CancellationToken.None);
            Assert.HasCount(1, history);
            Assert.AreEqual(
                SessionCompletionStatus.RecoveredAfterCrash,
                history[0].Status);
            Assert.AreEqual(0, history[0].ErrorCount);
            background.Process.Refresh();
            Assert.AreEqual(
                ProcessPriorityClass.Normal,
                background.Process.PriorityClass,
                "Stara sesja niczego na tym procesie nie zapisala w dzienniku, "
                    + "wiec odtwarzanie nie ma prawa go ruszac.");
        }
        finally
        {
            if (orchestrator is not null)
            {
                await orchestrator.DisposeAsync();
            }

            journal.Dispose();
            store.Dispose();
            await background.DisposeAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class SilentFrameRateProvider : IFrameRateProvider
    {
        public ValueTask<FrameRateSample> SampleAsync(
            IReadOnlyCollection<int> processIds,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FrameRateSample.Disabled());

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

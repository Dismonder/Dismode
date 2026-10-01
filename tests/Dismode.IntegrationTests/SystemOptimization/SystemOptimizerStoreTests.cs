using Dismode.Contracts.SystemOptimization;
using Dismode.Data.SystemOptimization;
using Microsoft.Data.Sqlite;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerStoreTests
{
    private const string OwnerSid = "S-1-5-21-1000";

    [TestMethod]
    public async Task InitializesIndependentSchemaVersionOneAndPersistsConsent()
    {
        using StoreContext context = new();

        await context.Store.InitializeAsync(CancellationToken.None);
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.IsTrue(await context.Store.HasConsentAsync(
            OwnerSid,
            CancellationToken.None));
        await using SqliteConnection connection = new(
            $"Data Source={context.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? version = await command.ExecuteScalarAsync();
        Assert.AreEqual(1L, version);
    }

    [TestMethod]
    public async Task ProfilesRoundTripWithoutSharingTheUserDatabase()
    {
        using StoreContext context = new();
        PerGameOptimizationProfile profile = new(
            Guid.NewGuid(),
            OwnerSid,
            "game-roblox",
            "hardware-a",
            [new("process.game.priority", 1, "above-normal")],
            Enabled: true,
            DateTimeOffset.UtcNow);

        await context.Store.UpsertPerGameProfileAsync(
            profile,
            CancellationToken.None);
        PerGameOptimizationProfile? loaded =
            await context.Store.GetPerGameProfileAsync(
                OwnerSid,
                profile.GameProfileId,
                CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(profile.ProfileId, loaded.ProfileId);
        Assert.AreEqual(profile.OwnerSid, loaded.OwnerSid);
        Assert.AreEqual(profile.GameProfileId, loaded.GameProfileId);
        Assert.AreEqual(profile.HardwareFingerprintHash, loaded.HardwareFingerprintHash);
        Assert.AreEqual(profile.Enabled, loaded.Enabled);
        CollectionAssert.AreEqual(
            profile.Selections.ToArray(),
            loaded.Selections.ToArray());
        Assert.AreEqual("system-optimizer.db", Path.GetFileName(context.DatabasePath));
        Assert.AreNotEqual("dismode-user.db", Path.GetFileName(context.DatabasePath));
    }

    [TestMethod]
    public async Task ListsPerGameProfilesOnlyForTheRequestedOwner()
    {
        using StoreContext context = new();
        PerGameOptimizationProfile first = CreatePerGameProfile(
            OwnerSid,
            "game-b");
        PerGameOptimizationProfile second = CreatePerGameProfile(
            OwnerSid,
            "game-a");
        PerGameOptimizationProfile foreign = CreatePerGameProfile(
            "S-1-5-21-2000",
            "game-c");
        await context.Store.UpsertPerGameProfileAsync(
            first,
            CancellationToken.None);
        await context.Store.UpsertPerGameProfileAsync(
            second,
            CancellationToken.None);
        await context.Store.UpsertPerGameProfileAsync(
            foreign,
            CancellationToken.None);

        IReadOnlyList<PerGameOptimizationProfile> profiles =
            await context.Store.ListPerGameProfilesAsync(
                OwnerSid,
                CancellationToken.None);

        string[] expected = ["game-a", "game-b"];
        CollectionAssert.AreEqual(
            expected,
            profiles.Select(profile => profile.GameProfileId).ToArray());
    }

    [TestMethod]
    public async Task OnlyOneMachineWideExperimentAndGlobalProfileCanBeActive()
    {
        using StoreContext context = new();
        ExperimentPlan firstExperiment = CreateExperiment(OwnerSid, "game-a");
        ExperimentPlan secondExperiment = CreateExperiment(
            "S-1-5-21-2000",
            "game-b");
        GlobalOptimizationProfile firstGlobal = CreateGlobalProfile(
            OwnerSid);
        GlobalOptimizationProfile secondGlobal = CreateGlobalProfile(
            "S-1-5-21-2000");

        await context.Store.UpsertExperimentAsync(
            firstExperiment,
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            context.Store.UpsertExperimentAsync(
                secondExperiment,
                CancellationToken.None).AsTask());
        await context.Store.UpsertGlobalProfileAsync(
            firstGlobal,
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            context.Store.UpsertGlobalProfileAsync(
                secondGlobal,
                CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task IdempotencyResultSurvivesStoreReopen()
    {
        using StoreContext context = new();
        Guid key = Guid.NewGuid();
        await context.Store.SaveIdempotentResponseAsync(
            OwnerSid,
            key,
            "PrepareSystemExperiment",
            new string('A', 64),
            "{\"experimentId\":\"one\"}",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        context.Reopen();

        string? response = await context.Store.GetIdempotentResponseAsync(
            OwnerSid,
            key,
            "PrepareSystemExperiment",
            new string('A', 64),
            CancellationToken.None);

        Assert.AreEqual("{\"experimentId\":\"one\"}", response);
    }

    [TestMethod]
    public async Task ReusedIdempotencyKeyRejectsChangedPayload()
    {
        using StoreContext context = new();
        Guid key = Guid.NewGuid();
        await context.Store.SaveIdempotentResponseAsync(
            OwnerSid,
            key,
            "PrepareSystemExperiment",
            new string('A', 64),
            "{\"experimentId\":\"one\"}",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            context.Store.GetIdempotentResponseAsync(
                OwnerSid,
                key,
                "PrepareSystemExperiment",
                new string('B', 64),
                CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task ActiveGameSessionRoundTripsAndRemainsMachineWideUnique()
    {
        using StoreContext context = new();
        ActiveGameOptimizationSession first = new(
            Guid.NewGuid(),
            OwnerSid,
            Guid.NewGuid(),
            "game-a",
            new string('A', 64),
            new string('B', 64),
            [new("process.game.priority", 1, "above-normal")],
            AppliedSelectionCount: 1,
            DateTimeOffset.UtcNow);
        ActiveGameOptimizationSession second = first with
        {
            ActivationId = Guid.NewGuid(),
            OwnerSid = "S-1-5-21-2000",
            GameProfileId = "game-b",
        };

        await context.Store.SetActiveGameSessionAsync(
            first,
            CancellationToken.None);
        ActiveGameOptimizationSession? loaded =
            await context.Store.GetActiveGameSessionAsync(
                CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(first.ActivationId, loaded.ActivationId);
        Assert.AreEqual(first.Selections.Single(), loaded.Selections.Single());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            context.Store.SetActiveGameSessionAsync(
                second,
                CancellationToken.None).AsTask());

        await context.Store.ClearActiveGameSessionAsync(
            first.ActivationId,
            CancellationToken.None);
        Assert.IsNull(await context.Store.GetActiveGameSessionAsync(
            CancellationToken.None));
    }

    [TestMethod]
    public async Task RuntimeTargetCanBeStoredForAProfileActivationWithoutAnExperiment()
    {
        using StoreContext context = new();
        Guid activationId = Guid.NewGuid();

        await context.Store.SaveRuntimeTargetAsync(
            activationId,
            "{\"processId\":1234}",
            CancellationToken.None);

        Assert.AreEqual(
            "{\"processId\":1234}",
            await context.Store.GetRuntimeTargetAsync(
                activationId,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ExistingVersionOneDatabaseMigratesLegacyRuntimeTargets()
    {
        using StoreContext context = new();
        Guid experimentId = Guid.NewGuid();
        await using (SqliteConnection connection = new(
            $"Data Source={context.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE ExperimentRuntimeTargets (
                    ExperimentId TEXT PRIMARY KEY NOT NULL,
                    TargetJson TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                INSERT INTO ExperimentRuntimeTargets
                    (ExperimentId, TargetJson, UpdatedAtUtc)
                VALUES
                    ($experimentId, '{"processId":4321}', '2026-08-29T00:00:00Z');
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue(
                "$experimentId",
                experimentId.ToString("D"));
            _ = await command.ExecuteNonQueryAsync();
        }

        await context.Store.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(
            "{\"processId\":4321}",
            await context.Store.GetRuntimeTargetAsync(
                experimentId,
                CancellationToken.None));
    }

    private static ExperimentPlan CreateExperiment(
        string ownerSid,
        string gameId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(
            Guid.NewGuid(),
            ownerSid,
            gameId,
            new string('A', 64),
            new string('B', 64),
            new("process.game.priority", 1, "above-normal"),
            BenchmarkVariant.Baseline,
            ExperimentState.WaitingForScene,
            RequiredPairs: 1,
            CompletedBaselineCaptures: 0,
            CompletedCandidateCaptures: 0,
            now,
            now);
    }

    private static PerGameOptimizationProfile CreatePerGameProfile(
        string ownerSid,
        string gameId) =>
        new(
            Guid.NewGuid(),
            ownerSid,
            gameId,
            "hardware-a",
            [new("process.game.priority", 1, "above-normal")],
            Enabled: true,
            DateTimeOffset.UtcNow);

    private static GlobalOptimizationProfile CreateGlobalProfile(
        string ownerSid) =>
        new(
            Guid.NewGuid(),
            ownerSid,
            "hardware-a",
            [new("power.hibernate", 1, "enabled")],
            Enabled: true,
            DateTimeOffset.UtcNow);

    private sealed class StoreContext : IDisposable
    {
        private readonly string _directoryPath = Path.Combine(
            Path.GetTempPath(),
            "Dismode-SystemOptimizerTests",
            Guid.NewGuid().ToString("N"));

        public StoreContext()
        {
            Directory.CreateDirectory(_directoryPath);
            DatabasePath = Path.Combine(
                _directoryPath,
                "system-optimizer.db");
            Store = new(DatabasePath);
        }

        public string DatabasePath { get; }

        public SqliteSystemOptimizerStore Store { get; private set; }

        public void Reopen()
        {
            Store.Dispose();
            Store = new(DatabasePath);
        }

        public void Dispose()
        {
            Store.Dispose();
            try
            {
                Directory.Delete(_directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

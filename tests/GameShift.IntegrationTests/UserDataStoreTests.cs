using GameShift.Core.Domain.Identifiers;
using GameShift.Core.History;
using GameShift.Core.Profiles;
using GameShift.Core.Updates;
using GameShift.Data.UserData;
using Microsoft.Data.Sqlite;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class UserDataStoreTests
{
    [TestMethod]
    public async Task ProfileCrudRoundTripsParameterizedValues()
    {
        using UserDataTestContext testContext = new();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        string artworkPath = Path.Combine(
            testContext.DirectoryPath,
            "cover.png");
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Game'); DROP TABLE GameProfiles;--",
            Path.Combine(testContext.DirectoryPath, "TestGame.exe"),
            new string('A', 64),
            testContext.DirectoryPath,
            ["--quality", "safe value with spaces"],
            OptimizationPreset.Safe,
            isEnabled: true,
            createdAt,
            createdAt,
            artworkPath);

        await testContext.Store.UpsertAsync(
            profile,
            CancellationToken.None);
        ManualGameProfile? found = await testContext.Store.FindAsync(
            profile.ProfileId,
            CancellationToken.None);
        IReadOnlyList<ManualGameProfile> listed =
            await testContext.Store.ListAsync(CancellationToken.None);

        Assert.IsNotNull(found);
        Assert.AreEqual(profile.DisplayName, found.DisplayName);
        Assert.AreEqual(profile.ExecutablePath, found.ExecutablePath);
        Assert.AreEqual(profile.ExecutableSha256, found.ExecutableSha256);
        Assert.AreEqual(artworkPath, found.ArtworkPath);
        Assert.AreEqual(profile.Preset, found.Preset);
        CollectionAssert.AreEqual(
            profile.LaunchArguments.ToArray(),
            found.LaunchArguments.ToArray());
        Assert.HasCount(1, listed);

        ManualGameProfile updated = new(
            profile.ProfileId,
            "Updated game",
            profile.ExecutablePath,
            profile.ExecutableSha256,
            profile.WorkingDirectory,
            profile.LaunchArguments,
            OptimizationPreset.Balanced,
            isEnabled: false,
            profile.CreatedAtUtc,
            profile.UpdatedAtUtc.AddMinutes(1),
            profile.ArtworkPath);
        await testContext.Store.UpsertAsync(
            updated,
            CancellationToken.None);

        ManualGameProfile? afterUpdate = await testContext.Store.FindAsync(
            profile.ProfileId,
            CancellationToken.None);
        Assert.IsNotNull(afterUpdate);
        Assert.AreEqual("Updated game", afterUpdate.DisplayName);
        Assert.AreEqual(OptimizationPreset.Balanced, afterUpdate.Preset);
        Assert.IsFalse(afterUpdate.IsEnabled);

        Assert.IsTrue(
            await testContext.Store.DeleteAsync(
                profile.ProfileId,
                CancellationToken.None));
        Assert.IsNull(
            await testContext.Store.FindAsync(
                profile.ProfileId,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task GameMetadataCrudRoundTripsLocalProviderValues()
    {
        using UserDataTestContext testContext = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Metadata Game",
            Path.Combine(testContext.DirectoryPath, "MetadataGame.exe"),
            new string('B', 64),
            testContext.DirectoryPath,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now,
            now,
            Path.Combine(testContext.DirectoryPath, "hero.jpg"));
        await testContext.Store.UpsertAsync(
            profile,
            CancellationToken.None);
        GameMetadata metadata = new(
            profile.ProfileId,
            "Steam",
            "12345",
            Path.Combine(testContext.DirectoryPath, "steam.exe"),
            now.AddHours(-2),
            totalPlaytimeMinutes: 321,
            Path.Combine(testContext.DirectoryPath, "hero.jpg"),
            now);

        await testContext.Store.UpsertMetadataAsync(
            metadata,
            CancellationToken.None);
        GameMetadata? loaded =
            await testContext.Store.FindMetadataAsync(
                profile.ProfileId,
                CancellationToken.None);
        IReadOnlyList<GameMetadata> listed =
            await testContext.Store.ListMetadataAsync(
                CancellationToken.None);

        Assert.AreEqual(metadata, loaded);
        Assert.HasCount(1, listed);

        Assert.IsTrue(
            await testContext.Store.DeleteAsync(
                profile.ProfileId,
                CancellationToken.None));
        Assert.IsNull(
            await testContext.Store.FindMetadataAsync(
                profile.ProfileId,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task HistoryIsOrderedAndRetentionDeletesOnlyExpiredRows()
    {
        using UserDataTestContext testContext = new();
        GameProfileId profileId = GameProfileId.Create();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionSummary oldSummary = CreateSummary(
            profileId,
            now.AddDays(-100),
            now.AddDays(-99));
        SessionSummary recentSummary = CreateSummary(
            profileId,
            now.AddHours(-2),
            now.AddHours(-1));

        await testContext.Store.AddAsync(
            oldSummary,
            CancellationToken.None);
        await testContext.Store.AddAsync(
            recentSummary,
            CancellationToken.None);

        IReadOnlyList<SessionSummary> beforeRetention =
            await testContext.Store.ListRecentAsync(
                maximumCount: 10,
                CancellationToken.None);
        int deleted = await testContext.Store.DeleteEndedBeforeAsync(
            now.AddDays(-90),
            CancellationToken.None);
        IReadOnlyList<SessionSummary> afterRetention =
            await testContext.Store.ListRecentAsync(
                maximumCount: 10,
                CancellationToken.None);

        Assert.HasCount(2, beforeRetention);
        Assert.AreEqual(
            recentSummary.SessionId,
            beforeRetention[0].SessionId);
        Assert.AreEqual(1, deleted);
        Assert.HasCount(1, afterRetention);
        Assert.AreEqual(
            recentSummary.SessionId,
            afterRetention[0].SessionId);
    }

    [TestMethod]
    public async Task InitializationIsIdempotentAndUsesSeparateUserDatabase()
    {
        using UserDataTestContext testContext = new();

        await testContext.Store.InitializeAsync(CancellationToken.None);
        await testContext.Store.InitializeAsync(CancellationToken.None);

        Assert.IsTrue(File.Exists(testContext.DatabasePath));
        Assert.IsTrue(
            testContext.DatabasePath.StartsWith(
                testContext.DirectoryPath,
                StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            testContext.DatabasePath.EndsWith(
                "recovery.jsonl",
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task RepeatedHistoryWriteIsIdempotent()
    {
        using UserDataTestContext testContext = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionSummary summary = CreateSummary(
            GameProfileId.Create(),
            now.AddMinutes(-5),
            now);

        await testContext.Store.AddAsync(summary, CancellationToken.None);
        await testContext.Store.AddAsync(summary, CancellationToken.None);
        IReadOnlyList<SessionSummary> history =
            await testContext.Store.ListRecentAsync(
                maximumCount: 10,
                CancellationToken.None);

        Assert.HasCount(1, history);
        Assert.AreEqual(summary.SessionId, history[0].SessionId);
    }

    [TestMethod]
    public async Task HistoryFrameRateStatisticsRoundTrip()
    {
        using UserDataTestContext testContext = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionFrameRateStatistics statistics = new(
            sampleCount: 180,
            averageFramesPerSecond: 143.8d,
            averageFrameTimeMilliseconds: 6.95d,
            minimumFramesPerSecond: 112.4d,
            maximumFrameTimeMilliseconds: 13.7d);
        SessionSummary summary = CreateSummary(
            GameProfileId.Create(),
            now.AddMinutes(-30),
            now,
            statistics);

        await testContext.Store.AddAsync(summary, CancellationToken.None);
        IReadOnlyList<SessionSummary> history =
            await testContext.Store.ListRecentAsync(
                maximumCount: 10,
                CancellationToken.None);

        Assert.HasCount(1, history);
        Assert.AreEqual(statistics, history[0].FrameRateStatistics);
    }

    [TestMethod]
    public async Task PerformanceOverlayPreferencesRoundTripAndHaveDefaults()
    {
        using UserDataTestContext testContext = new();

        PerformanceOverlayPreferences defaults =
            await testContext.Store.LoadPerformanceOverlayPreferencesAsync(
                CancellationToken.None);
        Assert.IsTrue(defaults.IsEnabled);
        Assert.IsTrue(defaults.IsFpsTrackingEnabled);
        Assert.AreEqual(
            PerformanceOverlayPreferences.DefaultOpacityPercent,
            defaults.OpacityPercent);
        Assert.AreEqual(
            PerformanceOverlayPreferences.DefaultScalePercent,
            defaults.ScalePercent);
        Assert.AreEqual(
            PerformanceOverlayCorner.TopRight,
            defaults.Corner);
        Assert.AreEqual(
            PerformanceOverlayStyle.FullDeck,
            defaults.Style);
        Assert.AreEqual(
            PerformanceOverlayTheme.CyberNeon,
            defaults.Theme);

        DateTimeOffset updatedAtUtc =
            DateTimeOffset.UtcNow.AddMinutes(-1);
        PerformanceOverlayPreferences saved = new(
            isEnabled: false,
            isFpsTrackingEnabled: false,
            opacityPercent: 65,
            scalePercent: 125,
            PerformanceOverlayCorner.BottomLeft,
            PerformanceOverlayStyle.MinimalText,
            PerformanceOverlayTheme.CrimsonRed,
            updatedAtUtc);
        await testContext.Store.SavePerformanceOverlayPreferencesAsync(
            saved,
            CancellationToken.None);

        PerformanceOverlayPreferences loaded =
            await testContext.Store.LoadPerformanceOverlayPreferencesAsync(
                CancellationToken.None);
        Assert.IsFalse(loaded.IsEnabled);
        Assert.IsFalse(loaded.IsFpsTrackingEnabled);
        Assert.AreEqual(65, loaded.OpacityPercent);
        Assert.AreEqual(125, loaded.ScalePercent);
        Assert.AreEqual(
            PerformanceOverlayCorner.BottomLeft,
            loaded.Corner);
        Assert.AreEqual(
            PerformanceOverlayStyle.MinimalText,
            loaded.Style);
        Assert.AreEqual(
            PerformanceOverlayTheme.CrimsonRed,
            loaded.Theme);
        Assert.AreEqual(updatedAtUtc, loaded.UpdatedAtUtc);
    }

    [TestMethod]
    public async Task UpdatePreferencesRoundTripAndDefaultToMonthlyAutomaticChecks()
    {
        using UserDataTestContext testContext = new();

        UpdatePreferences defaults =
            await testContext.Store.LoadUpdatePreferencesAsync(
                CancellationToken.None);
        Assert.IsTrue(defaults.AutomaticChecksEnabled);
        Assert.IsTrue(defaults.AutomaticInstallEnabled);
        Assert.AreEqual("preview", defaults.Channel);
        Assert.IsNull(defaults.LastSuccessfulCheckAtUtc);

        DateTimeOffset checkedAtUtc = DateTimeOffset.UtcNow.AddHours(-2);
        UpdatePreferences saved = new(
            AutomaticChecksEnabled: false,
            AutomaticInstallEnabled: false,
            Channel: "stable",
            LastSuccessfulCheckAtUtc: checkedAtUtc,
            LastObservedVersion: "0.1.1",
            LastError: "offline",
            UpdatedAtUtc: checkedAtUtc);
        await testContext.Store.SaveUpdatePreferencesAsync(
            saved,
            CancellationToken.None);

        UpdatePreferences loaded =
            await testContext.Store.LoadUpdatePreferencesAsync(
                CancellationToken.None);
        Assert.AreEqual(saved, loaded);
    }

    [TestMethod]
    public async Task MissingOptimizationPreferencesDefaultToNormalPriority()
    {
        using UserDataTestContext testContext = new();

        GameOptimizationPreferences preferences =
            await testContext.Store.LoadOptimizationPreferencesAsync(
                GameProfileId.Create(),
                CancellationToken.None);

        Assert.AreEqual(
            SavedGamePriorityMode.Normal,
            preferences.GamePriority);
        Assert.IsEmpty(preferences.BackgroundRules);
    }

    [TestMethod]
    public async Task VersionFourHistoryMigratesWithoutInventingFrameRateData()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.UserDataTests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "version-four.db");
        Directory.CreateDirectory(directory);
        SessionSummary legacySummary = CreateSummary(
            GameProfileId.Create(),
            DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow);

        try
        {
            using (SqliteUserDataStore currentStore = new(databasePath))
            {
                await currentStore.AddAsync(
                    legacySummary,
                    CancellationToken.None);
            }

            await DowngradeSessionHistoryToVersionFourAsync(databasePath);

            using SqliteUserDataStore migratedStore = new(databasePath);
            IReadOnlyList<SessionSummary> history =
                await migratedStore.ListRecentAsync(
                    maximumCount: 10,
                    CancellationToken.None);

            Assert.HasCount(1, history);
            Assert.AreEqual(legacySummary.SessionId, history[0].SessionId);
            Assert.IsNull(history[0].FrameRateStatistics);
            Assert.AreEqual(
                11,
                await ReadMaximumSchemaVersionAsync(databasePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task VersionSevenProfilesMigrateWithoutLosingArtwork()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.UserDataTests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "version-seven.db");
        Directory.CreateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string artworkPath = Path.Combine(directory, "hero.png");
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Legacy profile",
            Path.Combine(directory, "Legacy.exe"),
            new string('C', 64),
            directory,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now,
            now,
            artworkPath);

        try
        {
            using (SqliteUserDataStore currentStore = new(databasePath))
            {
                await currentStore.UpsertAsync(
                    profile,
                    CancellationToken.None);
            }

            await DowngradeGameMetadataToVersionSevenAsync(databasePath);

            using SqliteUserDataStore migratedStore = new(databasePath);
            ManualGameProfile? migrated = await migratedStore.FindAsync(
                profile.ProfileId,
                CancellationToken.None);
            IReadOnlyList<GameMetadata> metadata =
                await migratedStore.ListMetadataAsync(
                    CancellationToken.None);

            Assert.IsNotNull(migrated);
            Assert.AreEqual(profile.ProfileId, migrated.ProfileId);
            Assert.AreEqual(artworkPath, migrated.ArtworkPath);
            Assert.IsEmpty(metadata);
            Assert.AreEqual(
                11,
                await ReadMaximumSchemaVersionAsync(databasePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializationRejectsDatabaseFromNewerVersion()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.UserDataTests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "newer.db");
        Directory.CreateDirectory(directory);

        try
        {
            await CreateMigrationOnlyDatabaseAsync(
                databasePath,
                version: 999);
            using SqliteUserDataStore store = new(databasePath);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store
                    .InitializeAsync(CancellationToken.None)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task InitializationRejectsIncompleteCurrentSchema()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.UserDataTests",
            Guid.NewGuid().ToString("N"));
        string databasePath = Path.Combine(directory, "incomplete.db");
        Directory.CreateDirectory(directory);

        try
        {
            await CreateMigrationOnlyDatabaseAsync(
                databasePath,
                version: 10);
            using SqliteUserDataStore store = new(databasePath);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store
                    .InitializeAsync(CancellationToken.None)
                    .AsTask());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CreateMigrationOnlyDatabaseAsync(
        string databasePath,
        int version)
    {
        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE SchemaMigrations (
                Version INTEGER NOT NULL PRIMARY KEY,
                AppliedAtUtc TEXT NOT NULL
            );
            INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
            VALUES ($version, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue(
            "$appliedAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DowngradeSessionHistoryToVersionFourAsync(
        string databasePath)
    {
        await using SqliteConnection connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE SessionSummariesV4 (
                SessionId TEXT NOT NULL PRIMARY KEY,
                ProfileId TEXT NOT NULL,
                GameDisplayName TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT NOT NULL,
                Status INTEGER NOT NULL CHECK (Status BETWEEN 1 AND 5),
                AppliedActionCount INTEGER NOT NULL
                    CHECK (AppliedActionCount >= 0),
                RestoredActionCount INTEGER NOT NULL
                    CHECK (RestoredActionCount >= 0
                        AND RestoredActionCount <= AppliedActionCount),
                ConflictCount INTEGER NOT NULL
                    CHECK (ConflictCount >= 0),
                ErrorCount INTEGER NOT NULL CHECK (ErrorCount >= 0)
            );

            INSERT INTO SessionSummariesV4 (
                SessionId, ProfileId, GameDisplayName, StartedAtUtc,
                EndedAtUtc, Status, AppliedActionCount, RestoredActionCount,
                ConflictCount, ErrorCount)
            SELECT SessionId, ProfileId, GameDisplayName, StartedAtUtc,
                   EndedAtUtc, Status, AppliedActionCount,
                   RestoredActionCount, ConflictCount, ErrorCount
            FROM SessionSummaries;

            DROP TABLE SessionSummaries;
            ALTER TABLE SessionSummariesV4 RENAME TO SessionSummaries;
            CREATE INDEX IX_SessionSummaries_EndedAtUtc
                ON SessionSummaries(EndedAtUtc DESC);
            DROP TABLE UpdatePreferences;
            DROP TABLE GameMetadata;
            ALTER TABLE GameProfiles DROP COLUMN ArtworkPath;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN Style;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN Theme;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN IsFpsTrackingEnabled;
            DELETE FROM SchemaMigrations WHERE Version IN (5, 6, 7, 8, 9, 10, 11);
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task DowngradeGameMetadataToVersionSevenAsync(
        string databasePath)
    {
        await using SqliteConnection connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DROP TABLE GameMetadata;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN Style;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN Theme;
            ALTER TABLE PerformanceOverlayPreferences DROP COLUMN IsFpsTrackingEnabled;
            DELETE FROM SchemaMigrations WHERE Version IN (8, 9, 10, 11);
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task<int> ReadMaximumSchemaVersionAsync(
        string databasePath)
    {
        await using SqliteConnection connection = CreateConnection(databasePath);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(Version) FROM SchemaMigrations;";
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection CreateConnection(string databasePath) =>
        new(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());

    private static SessionSummary CreateSummary(
        GameProfileId profileId,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        SessionFrameRateStatistics? frameRateStatistics = null) =>
        new(
            SessionId.Create(),
            profileId,
            "Harness Game",
            startedAt,
            endedAt,
            SessionCompletionStatus.Completed,
            appliedActionCount: 3,
            restoredActionCount: 3,
            conflictCount: 0,
            errorCount: 0,
            frameRateStatistics: frameRateStatistics);
}

internal sealed class UserDataTestContext : IDisposable
{
    internal UserDataTestContext()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "GameShift.UserDataTests",
            Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(DirectoryPath, "user.db");
        Store = new(DatabasePath);
    }

    internal string DirectoryPath { get; }

    internal string DatabasePath { get; }

    internal SqliteUserDataStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

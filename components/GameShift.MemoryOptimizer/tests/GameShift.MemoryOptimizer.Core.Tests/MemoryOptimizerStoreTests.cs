using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Core.Storage;
using Microsoft.Data.Sqlite;

namespace GameShift.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryOptimizerStoreTests
{
    private static readonly string[] ExpectedOneExclusion = ["one"];
    private string _directory = null!;
    private string _databasePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift.MemoryOptimizer.Tests",
            Guid.NewGuid().ToString("N"));
        _databasePath = Path.Combine(_directory, "memory.db");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistsNormalizedSettingsAndHistory()
    {
        await using (MemoryOptimizerStore store = new(_databasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await store.SaveSettingsAsync(
                new()
                {
                    AvailableMemoryThresholdPercent = 99,
                    ExcludedProcesses = ["one.exe", "ONE"],
                },
                CancellationToken.None);
            MemoryOptimizerSettings settings = await store.GetSettingsAsync(
                CancellationToken.None);
            Assert.AreEqual(50, settings.AvailableMemoryThresholdPercent);
            CollectionAssert.AreEqual(
                ExpectedOneExclusion,
                settings.ExcludedProcesses.ToArray());

            OptimizationResult result = CreateResult();
            await store.AddHistoryAsync(result, CancellationToken.None);
            IReadOnlyList<OptimizationResult> history =
                await store.GetHistoryAsync(10, CancellationToken.None);
            Assert.HasCount(1, history);
            Assert.AreEqual(result.OperationId, history[0].OperationId);
            Assert.AreEqual(
                result.AvailableMemoryDeltaBytes,
                history[0].AvailableMemoryDeltaBytes);
        }
    }

    [TestMethod]
    public async Task MigratesLegacySettingsAndRejectsFutureSchema()
    {
        Directory.CreateDirectory(_directory);
        await WriteLegacyDatabaseAsync(schemaVersion: 0);
        await using (MemoryOptimizerStore store = new(_databasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            MemoryOptimizerSettings settings = await store.GetSettingsAsync(
                CancellationToken.None);
            Assert.IsFalse(settings.AutomationEnabled);
            Assert.AreEqual(30, settings.CooldownMinutes);
            Assert.AreEqual(
                MemoryOptimizerSettings.BasicAreas,
                settings.AutomaticAreas);
        }

        File.Delete(_databasePath);
        await WriteLegacyDatabaseAsync(schemaVersion: 99);
        await using MemoryOptimizerStore future = new(_databasePath);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            future.InitializeAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SavesCompactPinWithoutChangingDatabaseSchema()
    {
        await using MemoryOptimizerStore store = new(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        int schemaBefore = await ReadDatabaseSchemaAsync();

        await store.SaveSettingsAsync(
            new()
            {
                CompactMode = true,
                CompactAlwaysOnTop = true,
            },
            CancellationToken.None);
        MemoryOptimizerSettings settings = await store.GetSettingsAsync(
            CancellationToken.None);
        int schemaAfter = await ReadDatabaseSchemaAsync();

        Assert.IsTrue(settings.CompactMode);
        Assert.IsTrue(settings.CompactAlwaysOnTop);
        Assert.AreEqual(schemaBefore, schemaAfter);
        Assert.AreEqual(1, schemaAfter);
    }

    private async Task<int> ReadDatabaseSchemaAsync()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT Value FROM Meta WHERE Key = 'schema-version';";
        object? value = await command.ExecuteScalarAsync();
        return int.Parse(
            Assert.IsInstanceOfType<string>(value),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task WriteLegacyDatabaseAsync(int schemaVersion)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Meta (
                Key TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL
            );
            CREATE TABLE Settings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Json TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            INSERT INTO Meta(Key, Value)
            VALUES ('schema-version', $schema);
            INSERT INTO Settings(Id, Json, UpdatedAtUtc)
            VALUES (1, '{"automationEnabled":false}', $updated);
            """;
        command.Parameters.AddWithValue(
            "$schema",
            schemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$updated",
            DateTimeOffset.UtcNow.ToString("O"));
        _ = await command.ExecuteNonQueryAsync();
    }

    private static OptimizationResult CreateResult()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemorySnapshot before = new(now, 1000, 200, 2000, 1000, 80);
        MemorySnapshot after = new(
            now.AddSeconds(1),
            1000,
            300,
            2000,
            1100,
            70);
        return new(
            Guid.NewGuid(),
            OptimizationTrigger.Manual,
            MemoryArea.WorkingSet,
            OptimizationState.Completed,
            now,
            now.AddSeconds(1),
            before,
            after,
            [new(MemoryArea.WorkingSet, true, TimeSpan.FromMilliseconds(10), null, "ok", 50)],
            "ok");
    }
}

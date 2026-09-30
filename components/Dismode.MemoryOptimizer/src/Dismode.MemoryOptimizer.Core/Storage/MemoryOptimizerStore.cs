using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Dismode.MemoryOptimizer.Core.Ipc;
using Dismode.MemoryOptimizer.Core.Models;
using Microsoft.Data.Sqlite;

namespace Dismode.MemoryOptimizer.Core.Storage;

public sealed class MemoryOptimizerStore : IAsyncDisposable
{
    private const int CurrentDatabaseSchema = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writer = new(1, 1);

    public MemoryOptimizerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath) ??
                throw new ArgumentException(
                    "Database path must have a parent directory.",
                    nameof(databasePath)));
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public static MemoryOptimizerStore ForUserSid(string userSid)
    {
        _ = MemoryOptimizerProtocol.CreatePipeName(userSid);
        string directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Dismode",
            "MemoryOptimizer",
            userSid);
        EnsureUserDirectorySecurity(directory, userSid);
        return new(Path.Combine(directory, "memory-optimizer.db"));
    }

    public static string GetComponentStatusPath(string userSid)
    {
        _ = MemoryOptimizerProtocol.CreatePipeName(userSid);
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Dismode",
            "MemoryOptimizer",
            userSid,
            "component-status-v1.json");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(
                cancellationToken).ConfigureAwait(false);
            int? existingSchema = await ReadSchemaVersionAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            if (existingSchema is < 0 or > CurrentDatabaseSchema)
            {
                throw new InvalidDataException(
                    $"Unsupported Memory Optimizer database schema {existingSchema}.");
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                CREATE TABLE IF NOT EXISTS Meta (
                    Key TEXT PRIMARY KEY NOT NULL,
                    Value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS Settings (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Json TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS History (
                    OperationId TEXT PRIMARY KEY NOT NULL,
                    Trigger INTEGER NOT NULL,
                    Areas INTEGER NOT NULL,
                    State INTEGER NOT NULL,
                    StartedAtUtc TEXT NOT NULL,
                    CompletedAtUtc TEXT NOT NULL,
                    AvailableDeltaBytes INTEGER NOT NULL,
                    WorkingSetBytesReleased INTEGER NOT NULL,
                    ResultJson TEXT NOT NULL
                );
                INSERT INTO Meta(Key, Value)
                VALUES ('schema-version', $schema)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            command.Parameters.AddWithValue(
                "$schema",
                CurrentDatabaseSchema.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            _ = await command.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<MemoryOptimizerSettings> GetSettingsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM Settings WHERE Id = 1;";
        object? value = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        if (value is not string json)
        {
            return MemoryOptimizerSettings.Normalize(new());
        }

        try
        {
            MemoryOptimizerSettings? settings =
                JsonSerializer.Deserialize<MemoryOptimizerSettings>(
                    json,
                    MemoryOptimizerProtocol.JsonOptions);
            return MemoryOptimizerSettings.Normalize(settings ?? new());
        }
        catch (JsonException)
        {
            return MemoryOptimizerSettings.Normalize(new());
        }
    }

    public async Task SaveSettingsAsync(
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken)
    {
        settings = MemoryOptimizerSettings.Normalize(settings);
        string json = JsonSerializer.Serialize(
            settings,
            MemoryOptimizerProtocol.JsonOptions);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Settings(Id, Json, UpdatedAtUtc)
                VALUES (1, $json, $updated)
                ON CONFLICT(Id) DO UPDATE SET
                    Json = excluded.Json,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue(
                "$updated",
                DateTimeOffset.UtcNow.ToString("O"));
            _ = await command.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task AddHistoryAsync(
        OptimizationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        string json = JsonSerializer.Serialize(
            result,
            MemoryOptimizerProtocol.JsonOptions);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenAsync(
                cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                connection.BeginTransaction();
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO History(
                    OperationId, Trigger, Areas, State, StartedAtUtc,
                    CompletedAtUtc, AvailableDeltaBytes,
                    WorkingSetBytesReleased, ResultJson)
                VALUES (
                    $id, $trigger, $areas, $state, $started, $completed,
                    $availableDelta, $workingSetReleased, $json);
                """;
            insert.Parameters.AddWithValue("$id", result.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$trigger", (int)result.Trigger);
            insert.Parameters.AddWithValue("$areas", (int)result.RequestedAreas);
            insert.Parameters.AddWithValue("$state", (int)result.State);
            insert.Parameters.AddWithValue(
                "$started",
                result.StartedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue(
                "$completed",
                result.CompletedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue(
                "$availableDelta",
                result.AvailableMemoryDeltaBytes);
            insert.Parameters.AddWithValue(
                "$workingSetReleased",
                checked((long)Math.Min(
                    result.WorkingSetBytesReleased,
                    (ulong)long.MaxValue)));
            insert.Parameters.AddWithValue("$json", json);
            _ = await insert.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);

            await using SqliteCommand prune = connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM History
                WHERE OperationId NOT IN (
                    SELECT OperationId FROM History
                    ORDER BY CompletedAtUtc DESC
                    LIMIT 1000
                );
                """;
            _ = await prune.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<IReadOnlyList<OptimizationResult>> GetHistoryAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 500);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ResultJson FROM History
            ORDER BY CompletedAtUtc DESC
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);
        List<OptimizationResult> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                OptimizationResult? result =
                    JsonSerializer.Deserialize<OptimizationResult>(
                        reader.GetString(0),
                        MemoryOptimizerProtocol.JsonOptions);
                if (result is not null)
                {
                    results.Add(result);
                }
            }
            catch (JsonException)
            {
                // A corrupt row is isolated; later valid history remains readable.
            }
        }

        return results;
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int?> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand table = connection.CreateCommand();
        table.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type = 'table' AND name = 'Meta';
            """;
        long tableCount = (long)(await table.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false) ?? 0L);
        if (tableCount == 0)
        {
            return null;
        }

        await using SqliteCommand version = connection.CreateCommand();
        version.CommandText =
            "SELECT Value FROM Meta WHERE Key = 'schema-version';";
        object? value = await version.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return 0;
        }

        if (value is not string text || !int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsed))
        {
            throw new InvalidDataException(
                "Memory Optimizer database schema is malformed.");
        }

        return parsed;
    }

    private static void EnsureUserDirectorySecurity(
        string directory,
        string userSid)
    {
        DirectoryInfo info = Directory.CreateDirectory(directory);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit |
            InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new(
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new(
            new SecurityIdentifier(userSid),
            FileSystemRights.ReadAndExecute |
                FileSystemRights.ReadAttributes |
                FileSystemRights.ReadPermissions,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

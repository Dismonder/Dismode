using System.Data;
using System.Globalization;
using System.Text.Json;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.History;
using GameShift.Core.Profiles;
using GameShift.Core.Updates;
using GameShift.Data.Storage;
using Microsoft.Data.Sqlite;

namespace GameShift.Data.UserData;

public sealed class SqliteUserDataStore :
    IGameProfileRepository,
    IGameMetadataRepository,
    IGameOptimizationPreferencesRepository,
    ISessionHistoryRepository,
    IUpdatePreferencesRepository,
    IGameDetectionPreferencesRepository,
    IDisposable
{
    private const int CurrentSchemaVersion = 12;
    private const int MaximumHistoryPageSize = 1000;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteUserDataStore(string? databasePath = null)
    {
        _databasePath = Path.GetFullPath(
            databasePath ?? GameShiftStoragePaths.UserDatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(_databasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The user database directory is invalid.");
            }

            Directory.CreateDirectory(directory);
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await VerifyDatabaseIntegrityAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await VerifyRequiredSchemaAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ManualGameProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, DisplayName, ExecutablePath, ExecutableSha256,
                   WorkingDirectory, LaunchArgumentsJson, Preset, IsEnabled,
                   CreatedAtUtc, UpdatedAtUtc, ArtworkPath
            FROM GameProfiles
            ORDER BY DisplayName COLLATE NOCASE, ProfileId;
            """;

        List<ManualGameProfile> profiles = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async ValueTask<ManualGameProfile?> FindAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, DisplayName, ExecutablePath, ExecutableSha256,
                   WorkingDirectory, LaunchArgumentsJson, Preset, IsEnabled,
                   CreatedAtUtc, UpdatedAtUtc, ArtworkPath
            FROM GameProfiles
            WHERE ProfileId = $profileId;
            """;
        command.Parameters.AddWithValue(
            "$profileId",
            profileId.Value.ToString("D"));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProfile(reader)
            : null;
    }

    public async ValueTask UpsertAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO GameProfiles (
                ProfileId, DisplayName, ExecutablePath, ExecutableSha256,
                WorkingDirectory, LaunchArgumentsJson, Preset, IsEnabled,
                CreatedAtUtc, UpdatedAtUtc, ArtworkPath)
            VALUES (
                $profileId, $displayName, $executablePath, $executableSha256,
                $workingDirectory, $launchArgumentsJson, $preset, $isEnabled,
                $createdAtUtc, $updatedAtUtc, $artworkPath)
            ON CONFLICT(ProfileId) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                ExecutablePath = excluded.ExecutablePath,
                ExecutableSha256 = excluded.ExecutableSha256,
                WorkingDirectory = excluded.WorkingDirectory,
                LaunchArgumentsJson = excluded.LaunchArgumentsJson,
                Preset = excluded.Preset,
                IsEnabled = excluded.IsEnabled,
                ArtworkPath = excluded.ArtworkPath,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        AddProfileParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<GameMetadata>> ListMetadataAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Source, ExternalId, LauncherPath,
                   LastPlayedAtUtc, TotalPlaytimeMinutes, HeroArtworkPath,
                   LastMetadataRefreshAtUtc
            FROM GameMetadata
            ORDER BY ProfileId;
            """;
        List<GameMetadata> metadata = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            metadata.Add(ReadGameMetadata(reader));
        }

        return metadata;
    }

    public async ValueTask<GameMetadata?> FindMetadataAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ProfileId, Source, ExternalId, LauncherPath,
                   LastPlayedAtUtc, TotalPlaytimeMinutes, HeroArtworkPath,
                   LastMetadataRefreshAtUtc
            FROM GameMetadata
            WHERE ProfileId = $profileId;
            """;
        command.Parameters.AddWithValue(
            "$profileId",
            profileId.Value.ToString("D"));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadGameMetadata(reader)
            : null;
    }

    public async ValueTask UpsertMetadataAsync(
        GameMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GameMetadata (
                ProfileId, Source, ExternalId, LauncherPath,
                LastPlayedAtUtc, TotalPlaytimeMinutes, HeroArtworkPath,
                LastMetadataRefreshAtUtc)
            VALUES (
                $profileId, $source, $externalId, $launcherPath,
                $lastPlayedAtUtc, $totalPlaytimeMinutes, $heroArtworkPath,
                $lastMetadataRefreshAtUtc)
            ON CONFLICT(ProfileId) DO UPDATE SET
                Source = excluded.Source,
                ExternalId = excluded.ExternalId,
                LauncherPath = excluded.LauncherPath,
                LastPlayedAtUtc = excluded.LastPlayedAtUtc,
                TotalPlaytimeMinutes = excluded.TotalPlaytimeMinutes,
                HeroArtworkPath = excluded.HeroArtworkPath,
                LastMetadataRefreshAtUtc =
                    excluded.LastMetadataRefreshAtUtc;
            """;
        AddGameMetadataParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                .ConfigureAwait(false);
        string profileIdText = profileId.Value.ToString("D");
        await using (SqliteCommand rules = connection.CreateCommand())
        {
            rules.Transaction = transaction;
            rules.CommandText =
                "DELETE FROM BackgroundProcessRules "
                + "WHERE ProfileId = $profileId;";
            rules.Parameters.AddWithValue("$profileId", profileIdText);
            await rules.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (SqliteCommand preferences = connection.CreateCommand())
        {
            preferences.Transaction = transaction;
            preferences.CommandText =
                "DELETE FROM GameOptimizationPreferences "
                + "WHERE ProfileId = $profileId;";
            preferences.Parameters.AddWithValue("$profileId", profileIdText);
            await preferences.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        int affected;
        await using (SqliteCommand profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText =
                "DELETE FROM GameProfiles WHERE ProfileId = $profileId;";
            profile.Parameters.AddWithValue("$profileId", profileIdText);
            affected = await profile.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async ValueTask<GameOptimizationPreferences>
        LoadOptimizationPreferencesAsync(
            GameProfileId profileId,
            CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        SavedGamePriorityMode priority =
            SavedGamePriorityMode.Normal;
        DateTimeOffset updatedAtUtc = DateTimeOffset.UtcNow;
        await using (SqliteCommand preference = connection.CreateCommand())
        {
            preference.CommandText =
                """
                SELECT GamePriority, UpdatedAtUtc
                FROM GameOptimizationPreferences
                WHERE ProfileId = $profileId;
                """;
            preference.Parameters.AddWithValue(
                "$profileId",
                profileId.Value.ToString("D"));
            await using SqliteDataReader reader =
                await preference.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                priority =
                    (SavedGamePriorityMode)reader.GetInt32(0);
                updatedAtUtc = ParseTimestamp(reader.GetString(1));
            }
        }

        List<SavedBackgroundProcessRule> rules = [];
        await using (SqliteCommand ruleQuery = connection.CreateCommand())
        {
            ruleQuery.CommandText =
                """
                SELECT ExecutablePath, ActionMode
                FROM BackgroundProcessRules
                WHERE ProfileId = $profileId
                ORDER BY ExecutablePath COLLATE NOCASE;
                """;
            ruleQuery.Parameters.AddWithValue(
                "$profileId",
                profileId.Value.ToString("D"));
            await using SqliteDataReader reader =
                await ruleQuery.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                rules.Add(
                    new(
                        reader.GetString(0),
                        (SavedBackgroundActionMode)reader.GetInt32(1)));
            }
        }

        return new(
            profileId,
            priority,
            rules,
            updatedAtUtc);
    }

    public async ValueTask SaveOptimizationPreferencesAsync(
        GameOptimizationPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection
                .BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken)
                .ConfigureAwait(false);
        string profileId = preferences.ProfileId.Value.ToString("D");
        await using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO GameOptimizationPreferences (
                    ProfileId, GamePriority, UpdatedAtUtc)
                VALUES ($profileId, $gamePriority, $updatedAtUtc)
                ON CONFLICT(ProfileId) DO UPDATE SET
                    GamePriority = excluded.GamePriority,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            upsert.Parameters.AddWithValue("$profileId", profileId);
            upsert.Parameters.AddWithValue(
                "$gamePriority",
                (int)preferences.GamePriority);
            upsert.Parameters.AddWithValue(
                "$updatedAtUtc",
                FormatTimestamp(preferences.UpdatedAtUtc));
            await upsert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (SqliteCommand deleteRules = connection.CreateCommand())
        {
            deleteRules.Transaction = transaction;
            deleteRules.CommandText =
                "DELETE FROM BackgroundProcessRules "
                + "WHERE ProfileId = $profileId;";
            deleteRules.Parameters.AddWithValue("$profileId", profileId);
            await deleteRules.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (SavedBackgroundProcessRule rule
                     in preferences.BackgroundRules)
        {
            await using SqliteCommand insertRule =
                connection.CreateCommand();
            insertRule.Transaction = transaction;
            insertRule.CommandText =
                """
                INSERT INTO BackgroundProcessRules (
                    ProfileId, ExecutablePath, ActionMode, UpdatedAtUtc)
                VALUES (
                    $profileId, $executablePath, $actionMode, $updatedAtUtc);
                """;
            insertRule.Parameters.AddWithValue("$profileId", profileId);
            insertRule.Parameters.AddWithValue(
                "$executablePath",
                rule.ExecutablePath);
            insertRule.Parameters.AddWithValue(
                "$actionMode",
                (int)rule.ActionMode);
            insertRule.Parameters.AddWithValue(
                "$updatedAtUtc",
                FormatTimestamp(preferences.UpdatedAtUtc));
            await insertRule.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PerformanceOverlayPreferences>
        LoadPerformanceOverlayPreferencesAsync(
            CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT IsEnabled, IsFpsTrackingEnabled, OpacityPercent,
                   ScalePercent, Corner, COALESCE(Style, 1),
                   COALESCE(Theme, 1),
                   UpdatedAtUtc
            FROM PerformanceOverlayPreferences
            WHERE SettingsKey = 1;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return PerformanceOverlayPreferences.CreateDefault();
        }

        return new(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            (PerformanceOverlayCorner)reader.GetInt32(4),
            (PerformanceOverlayStyle)reader.GetInt32(5),
            (PerformanceOverlayTheme)reader.GetInt32(6),
            ParseTimestamp(reader.GetString(7)));
    }

    public async ValueTask SavePerformanceOverlayPreferencesAsync(
        PerformanceOverlayPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PerformanceOverlayPreferences (
                SettingsKey, IsEnabled, IsFpsTrackingEnabled,
                OpacityPercent, ScalePercent, Corner, Style, Theme,
                UpdatedAtUtc)
            VALUES (
                1, $isEnabled, $isFpsTrackingEnabled, $opacityPercent,
                $scalePercent, $corner, $style, $theme, $updatedAtUtc)
            ON CONFLICT(SettingsKey) DO UPDATE SET
                IsEnabled = excluded.IsEnabled,
                IsFpsTrackingEnabled = excluded.IsFpsTrackingEnabled,
                OpacityPercent = excluded.OpacityPercent,
                ScalePercent = excluded.ScalePercent,
                Corner = excluded.Corner,
                Style = excluded.Style,
                Theme = excluded.Theme,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$isEnabled",
            preferences.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$isFpsTrackingEnabled",
            preferences.IsFpsTrackingEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$opacityPercent",
            preferences.OpacityPercent);
        command.Parameters.AddWithValue(
            "$scalePercent",
            preferences.ScalePercent);
        command.Parameters.AddWithValue(
            "$corner",
            (int)preferences.Corner);
        command.Parameters.AddWithValue(
            "$style",
            (int)preferences.Style);
        command.Parameters.AddWithValue(
            "$theme",
            (int)preferences.Theme);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(preferences.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<UpdatePreferences> LoadUpdatePreferencesAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT AutomaticChecksEnabled, AutomaticInstallEnabled,
                   Channel, LastSuccessfulCheckAtUtc, LastObservedVersion,
                   LastError, UpdatedAtUtc
            FROM UpdatePreferences
            WHERE SettingsKey = 1;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return UpdatePreferences.CreateDefault();
        }

        return new(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.GetString(2),
            reader.IsDBNull(3)
                ? null
                : ParseTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ParseTimestamp(reader.GetString(6)));
    }

    public async ValueTask SaveUpdatePreferencesAsync(
        UpdatePreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Channel is not ("preview" or "stable"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferences),
                "Only preview and stable update channels are supported.");
        }

        if (preferences.LastObservedVersion is { Length: > 32 }
            || preferences.LastError is { Length: > 1000 })
        {
            throw new ArgumentOutOfRangeException(
                nameof(preferences),
                "Update preference text exceeds the storage limit.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO UpdatePreferences (
                SettingsKey, AutomaticChecksEnabled,
                AutomaticInstallEnabled, Channel,
                LastSuccessfulCheckAtUtc, LastObservedVersion,
                LastError, UpdatedAtUtc)
            VALUES (
                1, $automaticChecksEnabled, $automaticInstallEnabled,
                $channel, $lastSuccessfulCheckAtUtc,
                $lastObservedVersion, $lastError, $updatedAtUtc)
            ON CONFLICT(SettingsKey) DO UPDATE SET
                AutomaticChecksEnabled = excluded.AutomaticChecksEnabled,
                AutomaticInstallEnabled = excluded.AutomaticInstallEnabled,
                Channel = excluded.Channel,
                LastSuccessfulCheckAtUtc = excluded.LastSuccessfulCheckAtUtc,
                LastObservedVersion = excluded.LastObservedVersion,
                LastError = excluded.LastError,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$automaticChecksEnabled",
            preferences.AutomaticChecksEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$automaticInstallEnabled",
            preferences.AutomaticInstallEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$channel", preferences.Channel);
        command.Parameters.AddWithValue(
            "$lastSuccessfulCheckAtUtc",
            preferences.LastSuccessfulCheckAtUtc is null
                ? DBNull.Value
                : FormatTimestamp(
                    preferences.LastSuccessfulCheckAtUtc.Value));
        command.Parameters.AddWithValue(
            "$lastObservedVersion",
            (object?)preferences.LastObservedVersion ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastError",
            (object?)preferences.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(preferences.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GameDetectionPreferences>
        LoadGameDetectionPreferencesAsync(
            CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT AutoOptimizeDetectedGames, UpdatedAtUtc
            FROM GameDetectionPreferences
            WHERE SettingsKey = 1;
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return GameDetectionPreferences.CreateDefault();
        }

        return new(
            reader.GetBoolean(0),
            ParseTimestamp(reader.GetString(1)));
    }

    public async ValueTask SaveGameDetectionPreferencesAsync(
        GameDetectionPreferences preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO GameDetectionPreferences (
                SettingsKey, AutoOptimizeDetectedGames, UpdatedAtUtc)
            VALUES (1, $autoOptimizeDetectedGames, $updatedAtUtc)
            ON CONFLICT(SettingsKey) DO UPDATE SET
                AutoOptimizeDetectedGames = excluded.AutoOptimizeDetectedGames,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$autoOptimizeDetectedGames",
            preferences.AutoOptimizeDetectedGames ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(preferences.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask AddAsync(
        SessionSummary summary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO SessionSummaries (
                SessionId, ProfileId, GameDisplayName, StartedAtUtc,
                EndedAtUtc, Status, AppliedActionCount, RestoredActionCount,
                ConflictCount, ErrorCount, FrameRateSampleCount,
                AverageFramesPerSecond, AverageFrameTimeMilliseconds,
                MinimumFramesPerSecond, MaximumFrameTimeMilliseconds)
            VALUES (
                $sessionId, $profileId, $gameDisplayName, $startedAtUtc,
                $endedAtUtc, $status, $appliedActionCount,
                $restoredActionCount, $conflictCount, $errorCount,
                $frameRateSampleCount, $averageFramesPerSecond,
                $averageFrameTimeMilliseconds, $minimumFramesPerSecond,
                $maximumFrameTimeMilliseconds)
            ON CONFLICT(SessionId) DO NOTHING;
            """;
        command.Parameters.AddWithValue(
            "$sessionId",
            summary.SessionId.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$profileId",
            summary.ProfileId.Value.ToString("D"));
        command.Parameters.AddWithValue(
            "$gameDisplayName",
            summary.GameDisplayName);
        command.Parameters.AddWithValue(
            "$startedAtUtc",
            FormatTimestamp(summary.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$endedAtUtc",
            FormatTimestamp(summary.EndedAtUtc));
        command.Parameters.AddWithValue("$status", (int)summary.Status);
        command.Parameters.AddWithValue(
            "$appliedActionCount",
            summary.AppliedActionCount);
        command.Parameters.AddWithValue(
            "$restoredActionCount",
            summary.RestoredActionCount);
        command.Parameters.AddWithValue(
            "$conflictCount",
            summary.ConflictCount);
        command.Parameters.AddWithValue("$errorCount", summary.ErrorCount);
        SessionFrameRateStatistics? frameRate =
            summary.FrameRateStatistics;
        command.Parameters.AddWithValue(
            "$frameRateSampleCount",
            frameRate?.SampleCount ?? 0);
        command.Parameters.AddWithValue(
            "$averageFramesPerSecond",
            frameRate is null
                ? DBNull.Value
                : frameRate.AverageFramesPerSecond);
        command.Parameters.AddWithValue(
            "$averageFrameTimeMilliseconds",
            frameRate is null
                ? DBNull.Value
                : frameRate.AverageFrameTimeMilliseconds);
        command.Parameters.AddWithValue(
            "$minimumFramesPerSecond",
            frameRate is null
                ? DBNull.Value
                : frameRate.MinimumFramesPerSecond);
        command.Parameters.AddWithValue(
            "$maximumFrameTimeMilliseconds",
            frameRate is null
                ? DBNull.Value
                : frameRate.MaximumFrameTimeMilliseconds);
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SessionSummary>> ListRecentAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > MaximumHistoryPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                $"History page size must be between 1 and "
                + $"{MaximumHistoryPageSize}.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SessionId, ProfileId, GameDisplayName, StartedAtUtc,
                   EndedAtUtc, Status, AppliedActionCount,
                   RestoredActionCount, ConflictCount, ErrorCount,
                   FrameRateSampleCount, AverageFramesPerSecond,
                   AverageFrameTimeMilliseconds, MinimumFramesPerSecond,
                   MaximumFrameTimeMilliseconds
            FROM SessionSummaries
            ORDER BY EndedAtUtc DESC, SessionId
            LIMIT $maximumCount;
            """;
        command.Parameters.AddWithValue("$maximumCount", maximumCount);

        List<SessionSummary> summaries = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(ReadSummary(reader));
        }

        return summaries;
    }

    public async ValueTask<int> DeleteEndedBeforeAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM SessionSummaries WHERE EndedAtUtc < $cutoffUtc;";
        command.Parameters.AddWithValue(
            "$cutoffUtc",
            FormatTimestamp(cutoffUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _initializationGate.Dispose();
        _disposed = true;
    }

    private SqliteConnection CreateConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(_connectionString)
        {
            DefaultTimeout = 5,
        };
    }

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA secure_delete = ON;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask VerifyDatabaseIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        object? result = await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The user database failed its SQLite integrity check.");
        }
    }

    private static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL;";
            await journalMode.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await using (SqliteCommand migrationTable = connection.CreateCommand())
        {
            migrationTable.CommandText =
                """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Version INTEGER NOT NULL PRIMARY KEY,
                    AppliedAtUtc TEXT NOT NULL
                );
                """;
            await migrationTable.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        int existingVersion;
        await using (SqliteCommand versionQuery = connection.CreateCommand())
        {
            versionQuery.CommandText =
                "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations;";
            object? scalar = await versionQuery
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = Convert.ToInt32(
                scalar,
                CultureInfo.InvariantCulture);
        }

        if (existingVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "The user database was created by a newer GameShift version.");
        }

        if (existingVersion == CurrentSchemaVersion)
        {
            return;
        }

        if (existingVersion < 1)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE GameProfiles (
                    ProfileId TEXT NOT NULL PRIMARY KEY,
                    DisplayName TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL COLLATE NOCASE,
                    ExecutableSha256 TEXT NOT NULL,
                    WorkingDirectory TEXT NOT NULL,
                    LaunchArgumentsJson TEXT NOT NULL,
                    Preset INTEGER NOT NULL CHECK (Preset IN (1, 2)),
                    IsEnabled INTEGER NOT NULL CHECK (IsEnabled IN (0, 1)),
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IX_GameProfiles_ExecutablePath
                    ON GameProfiles(ExecutablePath);

                CREATE TABLE SessionSummaries (
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

                CREATE INDEX IX_SessionSummaries_EndedAtUtc
                    ON SessionSummaries(EndedAtUtc DESC);

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (1, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 1;
        }

        if (existingVersion < 2)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE GameOptimizationPreferences (
                    ProfileId TEXT NOT NULL PRIMARY KEY,
                    GamePriority INTEGER NOT NULL
                        CHECK (GamePriority BETWEEN 1 AND 3),
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (ProfileId)
                        REFERENCES GameProfiles(ProfileId)
                        ON DELETE CASCADE
                );

                CREATE TABLE BackgroundProcessRules (
                    ProfileId TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL COLLATE NOCASE,
                    ActionMode INTEGER NOT NULL
                        CHECK (ActionMode BETWEEN 1 AND 3),
                    UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (ProfileId, ExecutablePath),
                    FOREIGN KEY (ProfileId)
                        REFERENCES GameProfiles(ProfileId)
                        ON DELETE CASCADE
                );

                CREATE INDEX IX_BackgroundProcessRules_ProfileId
                    ON BackgroundProcessRules(ProfileId);

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (2, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 2;
        }

        if (existingVersion < 3)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE BackgroundProcessRulesV3 (
                    ProfileId TEXT NOT NULL,
                    ExecutablePath TEXT NOT NULL COLLATE NOCASE,
                    ActionMode INTEGER NOT NULL
                        CHECK (ActionMode IN (1, 2, 3, 4)),
                    UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (ProfileId, ExecutablePath),
                    FOREIGN KEY (ProfileId)
                        REFERENCES GameProfiles(ProfileId)
                        ON DELETE CASCADE
                );

                INSERT INTO BackgroundProcessRulesV3 (
                    ProfileId, ExecutablePath, ActionMode, UpdatedAtUtc)
                SELECT ProfileId, ExecutablePath, ActionMode, UpdatedAtUtc
                FROM BackgroundProcessRules;

                DROP INDEX IX_BackgroundProcessRules_ProfileId;
                DROP TABLE BackgroundProcessRules;
                ALTER TABLE BackgroundProcessRulesV3
                    RENAME TO BackgroundProcessRules;

                CREATE INDEX IX_BackgroundProcessRules_ProfileId
                    ON BackgroundProcessRules(ProfileId);

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (3, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 3;
        }

        if (existingVersion < 4)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE PerformanceOverlayPreferences (
                    SettingsKey INTEGER NOT NULL PRIMARY KEY
                        CHECK (SettingsKey = 1),
                    IsEnabled INTEGER NOT NULL
                        CHECK (IsEnabled IN (0, 1)),
                    OpacityPercent INTEGER NOT NULL
                        CHECK (OpacityPercent BETWEEN 20 AND 100),
                    ScalePercent INTEGER NOT NULL
                        CHECK (ScalePercent BETWEEN 75 AND 150),
                    Corner INTEGER NOT NULL
                        CHECK (Corner BETWEEN 1 AND 4),
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (4, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 4;
        }

        if (existingVersion < 5)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                ALTER TABLE SessionSummaries
                    ADD COLUMN FrameRateSampleCount INTEGER NOT NULL DEFAULT 0
                        CHECK (FrameRateSampleCount >= 0);
                ALTER TABLE SessionSummaries
                    ADD COLUMN AverageFramesPerSecond REAL
                        CHECK (AverageFramesPerSecond IS NULL
                            OR (AverageFramesPerSecond > 0
                                AND AverageFramesPerSecond <= 10000));
                ALTER TABLE SessionSummaries
                    ADD COLUMN AverageFrameTimeMilliseconds REAL
                        CHECK (AverageFrameTimeMilliseconds IS NULL
                            OR (AverageFrameTimeMilliseconds > 0
                                AND AverageFrameTimeMilliseconds <= 10000));
                ALTER TABLE SessionSummaries
                    ADD COLUMN MinimumFramesPerSecond REAL
                        CHECK (MinimumFramesPerSecond IS NULL
                            OR (MinimumFramesPerSecond > 0
                                AND MinimumFramesPerSecond <= 10000));
                ALTER TABLE SessionSummaries
                    ADD COLUMN MaximumFrameTimeMilliseconds REAL
                        CHECK (MaximumFrameTimeMilliseconds IS NULL
                            OR (MaximumFrameTimeMilliseconds > 0
                                AND MaximumFrameTimeMilliseconds <= 10000));

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (5, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 5;
        }

        if (existingVersion < 6)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE UpdatePreferences (
                    SettingsKey INTEGER NOT NULL PRIMARY KEY
                        CHECK (SettingsKey = 1),
                    AutomaticChecksEnabled INTEGER NOT NULL
                        CHECK (AutomaticChecksEnabled IN (0, 1)),
                    AutomaticInstallEnabled INTEGER NOT NULL
                        CHECK (AutomaticInstallEnabled IN (0, 1)),
                    Channel TEXT NOT NULL
                        CHECK (Channel IN ('preview', 'stable')),
                    LastSuccessfulCheckAtUtc TEXT,
                    LastObservedVersion TEXT,
                    LastError TEXT,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (6, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 6;
        }

        if (existingVersion < 7)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                ALTER TABLE GameProfiles ADD COLUMN ArtworkPath TEXT;

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (7, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 7;
        }

        if (existingVersion < 8)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE GameMetadata (
                    ProfileId TEXT NOT NULL PRIMARY KEY,
                    Source TEXT NOT NULL,
                    ExternalId TEXT,
                    LauncherPath TEXT,
                    LastPlayedAtUtc TEXT,
                    TotalPlaytimeMinutes INTEGER NOT NULL DEFAULT 0
                        CHECK (TotalPlaytimeMinutes >= 0),
                    HeroArtworkPath TEXT,
                    LastMetadataRefreshAtUtc TEXT NOT NULL,
                    FOREIGN KEY (ProfileId)
                        REFERENCES GameProfiles(ProfileId)
                        ON DELETE CASCADE
                );

                CREATE INDEX IX_GameMetadata_SourceExternalId
                    ON GameMetadata(Source, ExternalId);

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (8, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 8;
        }

        if (existingVersion < 9)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                ALTER TABLE PerformanceOverlayPreferences
                    ADD COLUMN Style INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE PerformanceOverlayPreferences
                    ADD COLUMN Theme INTEGER NOT NULL DEFAULT 1;

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (9, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 9;
        }

        if (existingVersion < 10)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE PerformanceOverlayPreferences_New (
                    SettingsKey INTEGER NOT NULL PRIMARY KEY
                        CHECK (SettingsKey = 1),
                    IsEnabled INTEGER NOT NULL
                        CHECK (IsEnabled IN (0, 1)),
                    OpacityPercent INTEGER NOT NULL
                        CHECK (OpacityPercent BETWEEN 20 AND 100),
                    ScalePercent INTEGER NOT NULL
                        CHECK (ScalePercent BETWEEN 75 AND 150),
                    Corner INTEGER NOT NULL
                        CHECK (Corner BETWEEN 1 AND 4),
                    Style INTEGER NOT NULL DEFAULT 1,
                    Theme INTEGER NOT NULL DEFAULT 1,
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO PerformanceOverlayPreferences_New (
                    SettingsKey, IsEnabled, OpacityPercent, ScalePercent, Corner, Style, Theme, UpdatedAtUtc)
                SELECT
                    SettingsKey, IsEnabled, MAX(20, MIN(100, OpacityPercent)), ScalePercent, Corner, Style, Theme, UpdatedAtUtc
                FROM PerformanceOverlayPreferences;

                DROP TABLE PerformanceOverlayPreferences;

                ALTER TABLE PerformanceOverlayPreferences_New
                    RENAME TO PerformanceOverlayPreferences;

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (10, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 10;
        }

        if (existingVersion < 11)
        {
            await using (SqliteCommand tableCheck = connection.CreateCommand())
            {
                tableCheck.CommandText =
                    "SELECT 1 FROM sqlite_master WHERE type = 'table' "
                    + "AND name = 'PerformanceOverlayPreferences' LIMIT 1;";
                object? tableExists = await tableCheck
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (tableExists is null)
                {
                    throw new InvalidDataException(
                        "The user database schema is incomplete.");
                }
            }

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                ALTER TABLE PerformanceOverlayPreferences
                    ADD COLUMN IsFpsTrackingEnabled INTEGER NOT NULL DEFAULT 1
                        CHECK (IsFpsTrackingEnabled IN (0, 1));

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (11, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 11;
        }

        if (existingVersion < 12)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection
                    .BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText =
                """
                CREATE TABLE GameDetectionPreferences (
                    SettingsKey INTEGER NOT NULL PRIMARY KEY
                        CHECK (SettingsKey = 1),
                    AutoOptimizeDetectedGames INTEGER NOT NULL
                        CHECK (AutoOptimizeDetectedGames IN (0, 1)),
                    UpdatedAtUtc TEXT NOT NULL
                );

                INSERT INTO SchemaMigrations (Version, AppliedAtUtc)
                VALUES (12, $appliedAtUtc);
                """;
            schema.Parameters.AddWithValue(
                "$appliedAtUtc",
                FormatTimestamp(DateTimeOffset.UtcNow));
            await schema.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            existingVersion = 12;
        }
    }

    private static async ValueTask VerifyRequiredSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteCommand objects = connection.CreateCommand();
            objects.CommandText =
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE (type = 'table' AND name IN (
                           'SchemaMigrations',
                           'GameProfiles',
                           'SessionSummaries',
                           'GameOptimizationPreferences',
                           'BackgroundProcessRules',
                           'PerformanceOverlayPreferences',
                           'UpdatePreferences',
                           'GameMetadata',
                           'GameDetectionPreferences'))
                   OR (type = 'index' AND name IN (
                           'IX_GameProfiles_ExecutablePath',
                           'IX_SessionSummaries_EndedAtUtc',
                           'IX_BackgroundProcessRules_ProfileId',
                           'IX_GameMetadata_SourceExternalId'));
                """;
            object? countResult = await objects
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            int objectCount = Convert.ToInt32(
                countResult,
                CultureInfo.InvariantCulture);
            if (objectCount != 13)
            {
                throw new InvalidDataException(
                    "The user database schema is incomplete.");
            }

            await using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText =
                """
                SELECT Version, AppliedAtUtc
                FROM SchemaMigrations
                LIMIT 0;

                SELECT ProfileId, DisplayName, ExecutablePath,
                       ExecutableSha256, WorkingDirectory,
                       LaunchArgumentsJson, Preset, IsEnabled,
                       CreatedAtUtc, UpdatedAtUtc, ArtworkPath
                FROM GameProfiles
                LIMIT 0;

                SELECT SessionId, ProfileId, GameDisplayName,
                       StartedAtUtc, EndedAtUtc, Status,
                       AppliedActionCount, RestoredActionCount,
                       ConflictCount, ErrorCount, FrameRateSampleCount,
                       AverageFramesPerSecond, AverageFrameTimeMilliseconds,
                       MinimumFramesPerSecond, MaximumFrameTimeMilliseconds
                FROM SessionSummaries
                LIMIT 0;

                SELECT ProfileId, GamePriority, UpdatedAtUtc
                FROM GameOptimizationPreferences
                LIMIT 0;

                SELECT ProfileId, ExecutablePath, ActionMode, UpdatedAtUtc
                FROM BackgroundProcessRules
                LIMIT 0;

                SELECT SettingsKey, IsEnabled, IsFpsTrackingEnabled,
                       OpacityPercent, ScalePercent, Corner, Style, Theme,
                       UpdatedAtUtc
                FROM PerformanceOverlayPreferences
                LIMIT 0;

                SELECT SettingsKey, AutomaticChecksEnabled,
                       AutomaticInstallEnabled, Channel,
                       LastSuccessfulCheckAtUtc, LastObservedVersion,
                       LastError, UpdatedAtUtc
                FROM UpdatePreferences
                LIMIT 0;

                SELECT ProfileId, Source, ExternalId, LauncherPath,
                       LastPlayedAtUtc, TotalPlaytimeMinutes,
                       HeroArtworkPath, LastMetadataRefreshAtUtc
                FROM GameMetadata
                LIMIT 0;

                SELECT SettingsKey, AutoOptimizeDetectedGames, UpdatedAtUtc
                FROM GameDetectionPreferences
                LIMIT 0;
                """;
            await columns.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                "The user database schema is invalid.",
                exception);
        }
    }

    private static void AddProfileParameters(
        SqliteCommand command,
        ManualGameProfile profile)
    {
        command.Parameters.AddWithValue(
            "$profileId",
            profile.ProfileId.Value.ToString("D"));
        command.Parameters.AddWithValue("$displayName", profile.DisplayName);
        command.Parameters.AddWithValue(
            "$executablePath",
            profile.ExecutablePath);
        command.Parameters.AddWithValue(
            "$executableSha256",
            profile.ExecutableSha256);
        command.Parameters.AddWithValue(
            "$workingDirectory",
            profile.WorkingDirectory);
        command.Parameters.AddWithValue(
            "$launchArgumentsJson",
            JsonSerializer.Serialize(profile.LaunchArguments));
        command.Parameters.AddWithValue("$preset", (int)profile.Preset);
        command.Parameters.AddWithValue(
            "$isEnabled",
            profile.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(profile.CreatedAtUtc));
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue(
            "$artworkPath",
            (object?)profile.ArtworkPath ?? DBNull.Value);
    }

    private static void AddGameMetadataParameters(
        SqliteCommand command,
        GameMetadata metadata)
    {
        command.Parameters.AddWithValue(
            "$profileId",
            metadata.ProfileId.Value.ToString("D"));
        command.Parameters.AddWithValue("$source", metadata.Source);
        command.Parameters.AddWithValue(
            "$externalId",
            (object?)metadata.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$launcherPath",
            (object?)metadata.LauncherPath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastPlayedAtUtc",
            metadata.LastPlayedAtUtc is DateTimeOffset lastPlayed
                ? FormatTimestamp(lastPlayed)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$totalPlaytimeMinutes",
            metadata.TotalPlaytimeMinutes);
        command.Parameters.AddWithValue(
            "$heroArtworkPath",
            (object?)metadata.HeroArtworkPath ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastMetadataRefreshAtUtc",
            FormatTimestamp(metadata.LastMetadataRefreshAtUtc));
    }

    private static ManualGameProfile ReadProfile(SqliteDataReader reader)
    {
        string[] arguments = JsonSerializer.Deserialize<string[]>(
            reader.GetString(5))
            ?? throw new InvalidDataException(
                "A profile launch-argument payload was null.");

        return new(
            new GameProfileId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            arguments,
            (OptimizationPreset)reader.GetInt32(6),
            reader.GetBoolean(7),
            ParseTimestamp(reader.GetString(8)),
            ParseTimestamp(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10));
    }

    private static GameMetadata ReadGameMetadata(SqliteDataReader reader) =>
        new(
            new GameProfileId(Guid.Parse(reader.GetString(0))),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4)
                ? null
                : ParseTimestamp(reader.GetString(4)),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ParseTimestamp(reader.GetString(7)));

    private static SessionSummary ReadSummary(SqliteDataReader reader)
    {
        int frameRateSampleCount = reader.GetInt32(10);
        SessionFrameRateStatistics? frameRateStatistics =
            frameRateSampleCount == 0
                ? null
                : new(
                    frameRateSampleCount,
                    reader.GetDouble(11),
                    reader.GetDouble(12),
                    reader.GetDouble(13),
                    reader.GetDouble(14));
        return new(
            new SessionId(Guid.Parse(reader.GetString(0))),
            new GameProfileId(Guid.Parse(reader.GetString(1))),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)),
            (SessionCompletionStatus)reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            frameRateStatistics);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(
            "O",
            CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}

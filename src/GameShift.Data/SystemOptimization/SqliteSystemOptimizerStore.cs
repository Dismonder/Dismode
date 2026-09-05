using System.Globalization;
using System.Text.Json;
using GameShift.Contracts.SystemOptimization;
using GameShift.Data.Storage;
using Microsoft.Data.Sqlite;

namespace GameShift.Data.SystemOptimization;

public sealed class SqliteSystemOptimizerStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumJsonCharacters = 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteSystemOptimizerStore(string? databasePath = null)
    {
        _databasePath = Path.GetFullPath(
            databasePath ?? GameShiftStoragePaths.SystemOptimizerDatabasePath);
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
                    "The system optimizer database directory is invalid.");
            }

            Directory.CreateDirectory(directory);
            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask SetConsentAsync(
        string ownerSid,
        bool accepted,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO UserConsent (OwnerSid, Accepted, UpdatedAtUtc)
            VALUES ($ownerSid, $accepted, $updatedAtUtc)
            ON CONFLICT(OwnerSid) DO UPDATE SET
                Accepted = excluded.Accepted,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(updatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<bool> HasConsentAsync(
        string ownerSid,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT Accepted FROM UserConsent WHERE OwnerSid = $ownerSid;";
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        object? result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return result is long accepted && accepted == 1;
    }

    public async ValueTask UpsertPerGameProfileAsync(
        PerGameOptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateSid(profile.OwnerSid);
        ValidateOpaqueIdentifier(profile.GameProfileId, nameof(profile.GameProfileId));
        string payload = SerializeBounded(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO PerGameProfiles
                (ProfileId, OwnerSid, GameProfileId, PayloadJson, Enabled, UpdatedAtUtc)
            VALUES
                ($profileId, $ownerSid, $gameProfileId, $payloadJson, $enabled, $updatedAtUtc)
            ON CONFLICT(OwnerSid, GameProfileId) DO UPDATE SET
                ProfileId = excluded.ProfileId,
                PayloadJson = excluded.PayloadJson,
                Enabled = excluded.Enabled,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$profileId", profile.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$ownerSid", profile.OwnerSid);
        command.Parameters.AddWithValue("$gameProfileId", profile.GameProfileId);
        command.Parameters.AddWithValue("$payloadJson", payload);
        command.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(profile.UpdatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PerGameOptimizationProfile?> GetPerGameProfileAsync(
        string ownerSid,
        string gameProfileId,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        ValidateOpaqueIdentifier(gameProfileId, nameof(gameProfileId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PayloadJson
            FROM PerGameProfiles
            WHERE OwnerSid = $ownerSid AND GameProfileId = $gameProfileId;
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$gameProfileId", gameProfileId);
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<PerGameOptimizationProfile>(json)
            : null;
    }

    public async ValueTask<IReadOnlyList<PerGameOptimizationProfile>>
        ListPerGameProfilesAsync(
            string ownerSid,
            CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT PayloadJson
            FROM PerGameProfiles
            WHERE OwnerSid = $ownerSid
            ORDER BY GameProfileId;
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        List<PerGameOptimizationProfile> profiles = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            profiles.Add(
                Deserialize<PerGameOptimizationProfile>(
                    reader.GetString(0)));
        }

        return profiles;
    }

    public async ValueTask SetActiveGameSessionAsync(
        ActiveGameOptimizationSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.ActivationId == Guid.Empty
            || session.ProfileId == Guid.Empty)
        {
            throw new ArgumentException(
                "Active game session identifiers cannot be empty.",
                nameof(session));
        }

        ValidateSid(session.OwnerSid);
        ValidateOpaqueIdentifier(
            session.GameProfileId,
            nameof(session.GameProfileId));
        ValidateSha256(
            session.GameExecutableHash,
            nameof(session.GameExecutableHash));
        ValidateSha256(
            session.HardwareFingerprintHash,
            nameof(session.HardwareFingerprintHash));
        string payload = SerializeBounded(session);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using (SqliteCommand activeCommand = connection.CreateCommand())
        {
            activeCommand.Transaction = transaction;
            activeCommand.CommandText =
                "SELECT ActivationId FROM ActiveGameSession WHERE SingletonId = 1;";
            object? active = await activeCommand.ExecuteScalarAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (active is string activeId
                && !StringComparer.OrdinalIgnoreCase.Equals(
                    activeId,
                    session.ActivationId.ToString("D")))
            {
                throw new InvalidOperationException(
                    "Another per-game optimization profile is already active.");
            }
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO ActiveGameSession
                (SingletonId, ActivationId, OwnerSid, PayloadJson, UpdatedAtUtc)
            VALUES
                (1, $activationId, $ownerSid, $payloadJson, $updatedAtUtc)
            ON CONFLICT(SingletonId) DO UPDATE SET
                ActivationId = excluded.ActivationId,
                OwnerSid = excluded.OwnerSid,
                PayloadJson = excluded.PayloadJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$activationId",
            session.ActivationId.ToString("D"));
        command.Parameters.AddWithValue("$ownerSid", session.OwnerSid);
        command.Parameters.AddWithValue("$payloadJson", payload);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async ValueTask<ActiveGameOptimizationSession?>
        GetActiveGameSessionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson FROM ActiveGameSession WHERE SingletonId = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<ActiveGameOptimizationSession>(json)
            : null;
    }

    public async ValueTask ClearActiveGameSessionAsync(
        Guid activationId,
        CancellationToken cancellationToken)
    {
        if (activationId == Guid.Empty)
        {
            throw new ArgumentException(
                "An activation ID cannot be empty.",
                nameof(activationId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM ActiveGameSession WHERE SingletonId = 1 AND ActivationId = $activationId;";
        command.Parameters.AddWithValue(
            "$activationId",
            activationId.ToString("D"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask UpsertGlobalProfileAsync(
        GlobalOptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateSid(profile.OwnerSid);
        string payload = SerializeBounded(profile);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (profile.Enabled)
        {
            await EnsureNoOtherActiveAsync(
                    connection,
                    transaction,
                    table: "GlobalProfiles",
                    idColumn: "ProfileId",
                    profile.ProfileId,
                    "Another global optimization profile is already active.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO GlobalProfiles
                (ProfileId, OwnerSid, PayloadJson, Enabled, UpdatedAtUtc)
            VALUES
                ($profileId, $ownerSid, $payloadJson, $enabled, $updatedAtUtc)
            ON CONFLICT(ProfileId) DO UPDATE SET
                OwnerSid = excluded.OwnerSid,
                PayloadJson = excluded.PayloadJson,
                Enabled = excluded.Enabled,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$profileId", profile.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$ownerSid", profile.OwnerSid);
        command.Parameters.AddWithValue("$payloadJson", payload);
        command.Parameters.AddWithValue("$enabled", profile.Enabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(profile.UpdatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async ValueTask<GlobalOptimizationProfile?> GetActiveGlobalProfileAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson FROM GlobalProfiles WHERE Enabled = 1 LIMIT 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<GlobalOptimizationProfile>(json)
            : null;
    }

    public async ValueTask UpsertExperimentAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ValidateSid(experiment.OwnerSid);
        ValidateOpaqueIdentifier(experiment.GameProfileId, nameof(experiment.GameProfileId));
        string payload = SerializeBounded(experiment);
        bool active = IsActive(experiment.State);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (active)
        {
            await EnsureNoOtherActiveAsync(
                    connection,
                    transaction,
                    table: "Experiments",
                    idColumn: "ExperimentId",
                    experiment.ExperimentId,
                    "Another system optimization experiment is already active.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO Experiments
                (ExperimentId, OwnerSid, GameProfileId, PayloadJson, IsActive, UpdatedAtUtc)
            VALUES
                ($experimentId, $ownerSid, $gameProfileId, $payloadJson, $isActive, $updatedAtUtc)
            ON CONFLICT(ExperimentId) DO UPDATE SET
                OwnerSid = excluded.OwnerSid,
                GameProfileId = excluded.GameProfileId,
                PayloadJson = excluded.PayloadJson,
                IsActive = excluded.IsActive,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$experimentId",
            experiment.ExperimentId.ToString("D"));
        command.Parameters.AddWithValue("$ownerSid", experiment.OwnerSid);
        command.Parameters.AddWithValue("$gameProfileId", experiment.GameProfileId);
        command.Parameters.AddWithValue("$payloadJson", payload);
        command.Parameters.AddWithValue("$isActive", active ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(experiment.UpdatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    public async ValueTask<ExperimentPlan?> GetActiveExperimentAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson FROM Experiments WHERE IsActive = 1 LIMIT 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<ExperimentPlan>(json)
            : null;
    }

    public async ValueTask<ExperimentPlan?> GetExperimentAsync(
        Guid experimentId,
        CancellationToken cancellationToken)
    {
        if (experimentId == Guid.Empty)
        {
            throw new ArgumentException(
                "The experiment ID cannot be empty.",
                nameof(experimentId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson FROM Experiments WHERE ExperimentId = $experimentId;";
        command.Parameters.AddWithValue(
            "$experimentId",
            experimentId.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<ExperimentPlan>(json)
            : null;
    }

    public async ValueTask SaveRuntimeTargetAsync(
        Guid operationId,
        string targetJson,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The operation ID cannot be empty.",
                nameof(operationId));
        }

        ValidateJsonLength(targetJson, nameof(targetJson));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RuntimeTargets
                (OperationId, TargetJson, UpdatedAtUtc)
            VALUES
                ($operationId, $targetJson, $updatedAtUtc)
            ON CONFLICT(OperationId) DO UPDATE SET
                TargetJson = excluded.TargetJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$operationId",
            operationId.ToString("D"));
        command.Parameters.AddWithValue("$targetJson", targetJson);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<string?> GetRuntimeTargetAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT TargetJson
            FROM RuntimeTargets
            WHERE OperationId = $operationId;
            """;
        command.Parameters.AddWithValue(
            "$operationId",
            operationId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false) as string;
    }

    public async ValueTask AddBenchmarkCaptureAsync(
        Guid experimentId,
        BenchmarkCapture capture,
        GameShift.Core.SystemOptimization.BenchmarkMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(metrics);
        if (experimentId == Guid.Empty || capture.CaptureId == Guid.Empty)
        {
            throw new ArgumentException(
                "Experiment and capture IDs must be non-empty.");
        }

        string captureJson = SerializeBounded(capture);
        string metricsJson = SerializeBounded(metrics);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BenchmarkCaptures
                (CaptureId, ExperimentId, Variant, CaptureJson, MetricsJson, CreatedAtUtc)
            VALUES
                ($captureId, $experimentId, $variant, $captureJson, $metricsJson, $createdAtUtc)
            ON CONFLICT(CaptureId) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$captureId", capture.CaptureId.ToString("D"));
        command.Parameters.AddWithValue("$experimentId", experimentId.ToString("D"));
        command.Parameters.AddWithValue("$variant", (int)capture.Variant);
        command.Parameters.AddWithValue("$captureJson", captureJson);
        command.Parameters.AddWithValue("$metricsJson", metricsJson);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<GameShift.Core.SystemOptimization.BenchmarkMetrics>>
        ListBenchmarkMetricsAsync(
            Guid experimentId,
            BenchmarkVariant variant,
            CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MetricsJson
            FROM BenchmarkCaptures
            WHERE ExperimentId = $experimentId AND Variant = $variant
            ORDER BY CreatedAtUtc, CaptureId;
            """;
        command.Parameters.AddWithValue("$experimentId", experimentId.ToString("D"));
        command.Parameters.AddWithValue("$variant", (int)variant);
        List<GameShift.Core.SystemOptimization.BenchmarkMetrics> metrics = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            metrics.Add(
                Deserialize<GameShift.Core.SystemOptimization.BenchmarkMetrics>(
                    reader.GetString(0)));
        }

        return metrics;
    }

    public async ValueTask SaveBenchmarkResultAsync(
        BenchmarkResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        string resultJson = SerializeBounded(result);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BenchmarkResults (ExperimentId, ResultJson, CreatedAtUtc)
            VALUES ($experimentId, $resultJson, $createdAtUtc)
            ON CONFLICT(ExperimentId) DO UPDATE SET
                ResultJson = excluded.ResultJson,
                CreatedAtUtc = excluded.CreatedAtUtc;
            """;
        command.Parameters.AddWithValue(
            "$experimentId",
            result.ExperimentId.ToString("D"));
        command.Parameters.AddWithValue("$resultJson", resultJson);
        command.Parameters.AddWithValue(
            "$createdAtUtc",
            FormatTimestamp(result.EvaluatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<BenchmarkResult?> GetBenchmarkResultAsync(
        Guid experimentId,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT ResultJson FROM BenchmarkResults WHERE ExperimentId = $experimentId;";
        command.Parameters.AddWithValue(
            "$experimentId",
            experimentId.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<BenchmarkResult>(json)
            : null;
    }

    public async ValueTask SetRecoveryStatusAsync(
        RecoveryStatus status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        string payload = SerializeBounded(status);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RecoveryState (SingletonId, PayloadJson, UpdatedAtUtc)
            VALUES (1, $payloadJson, $updatedAtUtc)
            ON CONFLICT(SingletonId) DO UPDATE SET
                PayloadJson = excluded.PayloadJson,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$payloadJson", payload);
        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            FormatTimestamp(DateTimeOffset.UtcNow));
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<RecoveryStatus> GetRecoveryStatusAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT PayloadJson FROM RecoveryState WHERE SingletonId = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is string json
            ? Deserialize<RecoveryStatus>(json)
            : new(
                RecoveryPhase.Baseline,
                ExperimentId: null,
                PhaseEnteredAtUtc: null,
                IsJournalClean: true,
                HasConflicts: false,
                ConflictTargets: [],
                "Brak aktywnych zmian systemowych.");
    }

    public async ValueTask<long> AppendHistoryAsync(
        string ownerSid,
        string operationKind,
        string? targetId,
        bool success,
        string details,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        ValidateOpaqueIdentifier(operationKind, nameof(operationKind));
        if (targetId is not null)
        {
            ValidateOpaqueIdentifier(targetId, nameof(targetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        if (details.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(details),
                details.Length,
                "History details exceed the 4096-character limit.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO OperationHistory
                (OwnerSid, OperationKind, TargetId, Success, Details, CreatedAtUtc)
            VALUES
                ($ownerSid, $operationKind, $targetId, $success, $details, $createdAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$operationKind", operationKind);
        command.Parameters.AddWithValue(
            "$targetId",
            targetId is null ? DBNull.Value : targetId);
        command.Parameters.AddWithValue("$success", success ? 1 : 0);
        command.Parameters.AddWithValue("$details", details);
        command.Parameters.AddWithValue("$createdAtUtc", FormatTimestamp(createdAtUtc));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    public async ValueTask<IReadOnlyList<SystemOptimizationHistoryRecord>>
        ListHistoryAsync(
            string ownerSid,
            int maximumItems,
            CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        if (maximumItems is <= 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumItems),
                maximumItems,
                "History page size must be between 1 and 500.");
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Sequence, OwnerSid, OperationKind, TargetId, Success, Details, CreatedAtUtc
            FROM OperationHistory
            WHERE OwnerSid = $ownerSid
            ORDER BY Sequence DESC
            LIMIT $maximumItems;
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$maximumItems", maximumItems);
        List<SystemOptimizationHistoryRecord> records = [];
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(
                new(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4) == 1,
                    reader.GetString(5),
                    DateTimeOffset.Parse(
                        reader.GetString(6),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
        }

        return records;
    }

    public async ValueTask SaveIdempotentResponseAsync(
        string ownerSid,
        Guid idempotencyKey,
        string commandName,
        string requestSha256,
        string responseJson,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException(
                "The idempotency key cannot be empty.",
                nameof(idempotencyKey));
        }

        ValidateOpaqueIdentifier(commandName, nameof(commandName));
        ValidateSha256(requestSha256, nameof(requestSha256));
        ValidateJsonLength(responseJson, nameof(responseJson));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO IdempotentResponses
                (OwnerSid, IdempotencyKey, CommandName, RequestSha256, ResponseJson, CreatedAtUtc)
            VALUES
                ($ownerSid, $idempotencyKey, $commandName, $requestSha256, $responseJson, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey.ToString("D"));
        command.Parameters.AddWithValue("$commandName", commandName);
        command.Parameters.AddWithValue("$requestSha256", requestSha256);
        command.Parameters.AddWithValue("$responseJson", responseJson);
        command.Parameters.AddWithValue("$createdAtUtc", FormatTimestamp(createdAtUtc));
        int inserted = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (inserted == 0)
        {
            string? existing = await GetIdempotentResponseCoreAsync(
                    connection,
                    ownerSid,
                    idempotencyKey,
                    commandName,
                    requestSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(existing, responseJson))
            {
                throw new InvalidOperationException(
                    "The idempotency key is already associated with a different response.");
            }
        }
    }

    public async ValueTask<string?> GetIdempotentResponseAsync(
        string ownerSid,
        Guid idempotencyKey,
        string commandName,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        ValidateSid(ownerSid);
        ValidateOpaqueIdentifier(commandName, nameof(commandName));
        ValidateSha256(requestSha256, nameof(requestSha256));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenAsync(
            cancellationToken).ConfigureAwait(false);
        return await GetIdempotentResponseCoreAsync(
                connection,
                ownerSid,
                idempotencyKey,
                commandName,
                requestSha256,
                cancellationToken)
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

    private static bool IsActive(ExperimentState state) =>
        state is not (ExperimentState.Completed or ExperimentState.Failed);

    private static async ValueTask EnsureNoOtherActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        Guid requestedId,
        string error,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {idColumn} FROM {table} WHERE {(table == "Experiments" ? "IsActive" : "Enabled")} = 1 LIMIT 1;";
        object? active = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (active is string activeId
            && !StringComparer.OrdinalIgnoreCase.Equals(
                activeId,
                requestedId.ToString("D")))
        {
            throw new InvalidOperationException(error);
        }
    }

    private static async ValueTask<string?> GetIdempotentResponseCoreAsync(
        SqliteConnection connection,
        string ownerSid,
        Guid idempotencyKey,
        string commandName,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RequestSha256, ResponseJson
            FROM IdempotentResponses
            WHERE OwnerSid = $ownerSid
              AND IdempotencyKey = $idempotencyKey
              AND CommandName = $commandName;
            """;
        command.Parameters.AddWithValue("$ownerSid", ownerSid);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey.ToString("D"));
        command.Parameters.AddWithValue("$commandName", commandName);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                reader.GetString(0),
                requestSha256))
        {
            throw new InvalidOperationException(
                "The idempotency key was reused with a changed payload.");
        }

        return reader.GetString(1);
    }

    private async ValueTask<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        long existingVersion = Convert.ToInt64(
            await versionCommand.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (existingVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"System optimizer database schema {existingVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }

        if (existingVersion == CurrentSchemaVersion)
        {
            await EnsureSupplementalSchemaAsync(
                    connection,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE UserConsent (
                OwnerSid TEXT PRIMARY KEY NOT NULL,
                Accepted INTEGER NOT NULL CHECK (Accepted IN (0, 1)),
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE PerGameProfiles (
                ProfileId TEXT NOT NULL,
                OwnerSid TEXT NOT NULL,
                GameProfileId TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                Enabled INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (OwnerSid, GameProfileId)
            );

            CREATE TABLE GlobalProfiles (
                ProfileId TEXT PRIMARY KEY NOT NULL,
                OwnerSid TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                Enabled INTEGER NOT NULL CHECK (Enabled IN (0, 1)),
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IX_GlobalProfiles_OneActive
            ON GlobalProfiles (Enabled)
            WHERE Enabled = 1;

            CREATE TABLE Experiments (
                ExperimentId TEXT PRIMARY KEY NOT NULL,
                OwnerSid TEXT NOT NULL,
                GameProfileId TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                IsActive INTEGER NOT NULL CHECK (IsActive IN (0, 1)),
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IX_Experiments_OneActive
            ON Experiments (IsActive)
            WHERE IsActive = 1;

            CREATE TABLE BenchmarkCaptures (
                CaptureId TEXT PRIMARY KEY NOT NULL,
                ExperimentId TEXT NOT NULL,
                Variant INTEGER NOT NULL,
                CaptureJson TEXT NOT NULL,
                MetricsJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ExperimentId) REFERENCES Experiments (ExperimentId)
            );

            CREATE TABLE RuntimeTargets (
                OperationId TEXT PRIMARY KEY NOT NULL,
                TargetJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE BenchmarkResults (
                ExperimentId TEXT PRIMARY KEY NOT NULL,
                ResultJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ExperimentId) REFERENCES Experiments (ExperimentId)
            );

            CREATE TABLE OperationHistory (
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                OwnerSid TEXT NOT NULL,
                OperationKind TEXT NOT NULL,
                TargetId TEXT,
                Success INTEGER NOT NULL CHECK (Success IN (0, 1)),
                Details TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IdempotentResponses (
                OwnerSid TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL,
                CommandName TEXT NOT NULL,
                RequestSha256 TEXT NOT NULL,
                ResponseJson TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (OwnerSid, IdempotencyKey, CommandName)
            );

            CREATE TABLE RecoveryState (
                SingletonId INTEGER PRIMARY KEY NOT NULL CHECK (SingletonId = 1),
                PayloadJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ActiveGameSession (
                SingletonId INTEGER PRIMARY KEY NOT NULL CHECK (SingletonId = 1),
                ActivationId TEXT NOT NULL UNIQUE,
                OwnerSid TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            PRAGMA user_version=1;
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        transaction.Commit();
    }

    private static async ValueTask EnsureSupplementalSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS ActiveGameSession (
                SingletonId INTEGER PRIMARY KEY NOT NULL CHECK (SingletonId = 1),
                ActivationId TEXT NOT NULL UNIQUE,
                OwnerSid TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS RuntimeTargets (
                OperationId TEXT PRIMARY KEY NOT NULL,
                TargetJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'ExperimentRuntimeTargets';";
        object? legacyTable = await tableCommand.ExecuteScalarAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (legacyTable is null)
        {
            return;
        }

        await using SqliteCommand migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText =
            """
            INSERT OR IGNORE INTO RuntimeTargets
                (OperationId, TargetJson, UpdatedAtUtc)
            SELECT ExperimentId, TargetJson, UpdatedAtUtc
            FROM ExperimentRuntimeTargets;
            """;
        _ = await migrationCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SerializeBounded<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, SerializerOptions);
        ValidateJsonLength(json, nameof(value));
        return json;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions)
        ?? throw new InvalidDataException(
            $"Stored {typeof(T).Name} payload is empty or invalid.");

    private static void ValidateJsonLength(string json, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumJsonCharacters)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                json.Length,
                "The serialized payload exceeds the 1 MiB character limit.");
        }
    }

    private static void ValidateSid(string ownerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        if (ownerSid.Length > 184
            || ownerSid.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ArgumentException(
                "The owner SID has an invalid format.",
                nameof(ownerSid));
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "The value must be a 64-character SHA-256.",
                parameterName);
        }
    }

    private static void ValidateOpaqueIdentifier(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException(
                "The identifier must be a bounded opaque token.",
                parameterName);
        }
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameShift.Contracts.Protocol;
using GameShift.Contracts.SystemOptimization;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Journal;
using GameShift.Core.Recovery;
using GameShift.Core.Transactions;
using GameShift.Data.SystemOptimization;
using GameShift.Windows.Processes;
using GameShift.Windows.SystemOptimization;

namespace GameShift.SystemAgent.SystemOptimization;

internal sealed class WindowsSystemTweakRuntime :
    ISystemTweakRuntime,
    IDisposable
{
    private const int MaximumProcessesToInspect = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.General);

    private readonly IRecoveryJournal _journal;
    private readonly SqliteSystemOptimizerStore _store;
    private readonly ProcessIdentityProvider _processIdentityProvider = new();
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    internal WindowsSystemTweakRuntime(
        IRecoveryJournal journal,
        SqliteSystemOptimizerStore store)
    {
        _journal = journal;
        _store = store;
    }

    public void Dispose()
    {
        _singleFlight.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask<SystemTweakRuntimeResult> ApplyAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken)
    {
        if (!await _singleFlight.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false))
        {
            return SystemTweakRuntimeResult.Blocked(
                "Inna operacja systemowa jest już wykonywana.");
        }

        try
        {
            return request.Selection.TweakId switch
            {
                "process.game.priority" =>
                    await ApplyGamePriorityAsync(
                            request,
                            applicationIndex,
                            cancellationToken)
                        .ConfigureAwait(false),
                "process.game.power-throttling" =>
                    await ApplyGamePowerThrottlingAsync(
                            request,
                            applicationIndex,
                            cancellationToken)
                        .ConfigureAwait(false),
                "power.hibernate" =>
                    await ExecuteAsync(
                            new HibernateConfigurationAction(
                                CreateActionId(
                                    request.OperationId,
                                    applicationIndex),
                                desiredEnabled: StringComparer.Ordinal.Equals(
                                    request.Selection.Value,
                                    "enabled")),
                            CreateContext(
                                request.OperationId,
                                applicationIndex),
                            cancellationToken)
                        .ConfigureAwait(false),
                _ => SystemTweakRuntimeResult.Blocked(
                    "Bieżący katalog nie ma wykonawczego adaptera dla tej pozycji."),
            };
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public async ValueTask<SystemTweakRuntimeResult> RestoreAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken)
    {
        if (!await _singleFlight.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false))
        {
            return SystemTweakRuntimeResult.Blocked(
                "Inna operacja systemowa jest już wykonywana.");
        }

        try
        {
            return request.Selection.TweakId switch
            {
                "process.game.priority" =>
                    await RecoverGamePriorityAsync(
                            request,
                            applicationIndex,
                            cancellationToken)
                        .ConfigureAwait(false),
                "process.game.power-throttling" =>
                    await RecoverGamePowerThrottlingAsync(
                            request,
                            applicationIndex,
                            cancellationToken)
                        .ConfigureAwait(false),
                "power.hibernate" =>
                    await RecoverAsync(
                            new HibernateConfigurationAction(
                                CreateActionId(
                                    request.OperationId,
                                    applicationIndex),
                                desiredEnabled: StringComparer.Ordinal.Equals(
                                    request.Selection.Value,
                                    "enabled")),
                            CreateContext(
                                request.OperationId,
                                applicationIndex),
                            cancellationToken)
                        .ConfigureAwait(false),
                _ => SystemTweakRuntimeResult.Blocked(
                    "Bieżący katalog nie ma wykonawczego adaptera dla tej pozycji."),
            };
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    public async ValueTask<bool> IsOperationTargetActiveAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await LoadRuntimeTargetAsync(
                operationId,
                cancellationToken)
            .ConfigureAwait(false);
        return identity is not null
            && await _processIdentityProvider.MatchesRuntimeIdentityAsync(
                    identity,
                    cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<string> GetVerifiedStateHashAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken)
    {
        object state = request.Selection.TweakId switch
        {
            "process.game.priority" =>
                await ReadGamePriorityStateAsync(
                        request,
                        applicationIndex,
                        cancellationToken)
                    .ConfigureAwait(false),
            "process.game.power-throttling" =>
                await ReadGamePowerThrottlingStateAsync(
                        request,
                        applicationIndex,
                        cancellationToken)
                    .ConfigureAwait(false),
            "power.hibernate" =>
                await new HibernateConfigurationAction(
                        CreateActionId(request.OperationId, applicationIndex),
                        desiredEnabled: StringComparer.Ordinal.Equals(
                            request.Selection.Value,
                            "enabled"))
                    .ReadCurrentStateAsync(
                        CreateContext(request.OperationId, applicationIndex),
                        cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                "Bieżący katalog nie ma adaptera niezależnego readbacku dla tej pozycji."),
        };
        byte[] canonicalState = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                request.Selection.TweakId,
                request.Selection.Revision,
                State = state,
            },
            SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(canonicalState));
    }

    private async ValueTask<RuntimeProcessPriorityState>
        ReadGamePriorityStateAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
    {
        ProcessIdentity identity = await ResolveAndPersistGameProcessAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeProcessPriorityAction action = new(
            CreateActionId(request.OperationId, applicationIndex),
            identity,
            ProcessPriorityClass.AboveNormal,
            _processIdentityProvider);
        return await action.ReadCurrentStateAsync(
                CreateContext(request.OperationId, applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<RuntimeProcessEcoQosState>
        ReadGamePowerThrottlingStateAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
    {
        ProcessIdentity identity = await ResolveAndPersistGameProcessAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeProcessEcoQosAction action = new(
            CreateActionId(request.OperationId, applicationIndex),
            identity,
            desiredEnabled: false,
            _processIdentityProvider);
        return await action.ReadCurrentStateAsync(
                CreateContext(request.OperationId, applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ProcessIdentity> ResolveAndPersistGameProcessAsync(
        SystemTweakRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await ResolveGameProcessAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            throw new InvalidOperationException(
                "Nie znaleziono jednego zweryfikowanego procesu gry o zapisanym hash SHA-256.");
        }

        await SaveRuntimeTargetAsync(
                request.OperationId,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        return identity;
    }

    private async ValueTask<SystemTweakRuntimeResult> ApplyGamePriorityAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await ResolveGameProcessAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return SystemTweakRuntimeResult.Blocked(
                "Nie znaleziono jednego zweryfikowanego procesu gry o zapisanym hash SHA-256.");
        }

        await SaveRuntimeTargetAsync(
                request.OperationId,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeProcessPriorityAction action = new(
            CreateActionId(
                request.OperationId,
                applicationIndex),
            identity,
            ProcessPriorityClass.AboveNormal,
            _processIdentityProvider);
        return await ExecuteAsync(
                action,
                CreateContext(
                    request.OperationId,
                    applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SystemTweakRuntimeResult>
        ApplyGamePowerThrottlingAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await ResolveGameProcessAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return SystemTweakRuntimeResult.Blocked(
                "Nie znaleziono jednego zweryfikowanego procesu gry o zapisanym hash SHA-256.");
        }

        await SaveRuntimeTargetAsync(
                request.OperationId,
                identity,
                cancellationToken)
            .ConfigureAwait(false);
        RuntimeProcessEcoQosAction action = new(
            CreateActionId(
                request.OperationId,
                applicationIndex),
            identity,
            desiredEnabled: false,
            _processIdentityProvider);
        return await ExecuteAsync(
                action,
                CreateContext(
                    request.OperationId,
                    applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SystemTweakRuntimeResult> RecoverGamePriorityAsync(
        SystemTweakRuntimeRequest request,
        int applicationIndex,
        CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await LoadRuntimeTargetAsync(
                request.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return SystemTweakRuntimeResult.Blocked(
                "Brakuje serwerowego zapisu tożsamości procesu potrzebnego do recovery.");
        }

        RuntimeProcessPriorityAction action = new(
            CreateActionId(
                request.OperationId,
                applicationIndex),
            identity,
            ProcessPriorityClass.AboveNormal,
            _processIdentityProvider);
        return await RecoverAsync(
                action,
                CreateContext(
                    request.OperationId,
                    applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SystemTweakRuntimeResult>
        RecoverGamePowerThrottlingAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
    {
        ProcessIdentity? identity = await LoadRuntimeTargetAsync(
                request.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return SystemTweakRuntimeResult.Blocked(
                "Brakuje serwerowego zapisu tożsamości procesu potrzebnego do recovery.");
        }

        RuntimeProcessEcoQosAction action = new(
            CreateActionId(
                request.OperationId,
                applicationIndex),
            identity,
            desiredEnabled: false,
            _processIdentityProvider);
        return await RecoverAsync(
                action,
                CreateContext(
                    request.OperationId,
                    applicationIndex),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ProcessIdentity?> ResolveGameProcessAsync(
        SystemTweakRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        List<ProcessIdentity> matches = [];
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes
                .OrderBy(candidate => candidate.Id)
                .Take(MaximumProcessesToInspect))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProcessIdentity? identity =
                    await _processIdentityProvider.TryCaptureAsync(
                            process,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (identity is not null
                    && StringComparer.OrdinalIgnoreCase.Equals(
                        identity.UserSid,
                        request.OwnerSid)
                    && StringComparer.OrdinalIgnoreCase.Equals(
                        identity.ExecutableSha256,
                        request.GameExecutableHash))
                {
                    matches.Add(identity);
                    if (matches.Count > 1)
                    {
                        return null;
                    }
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return matches.SingleOrDefault();
    }

    private async ValueTask SaveRuntimeTargetAsync(
        Guid experimentId,
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(identity, SerializerOptions);
        await _store.SaveRuntimeTargetAsync(
                experimentId,
                json,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<ProcessIdentity?> LoadRuntimeTargetAsync(
        Guid experimentId,
        CancellationToken cancellationToken)
    {
        string? json = await _store.GetRuntimeTargetAsync(
                experimentId,
                cancellationToken)
            .ConfigureAwait(false);
        return json is null
            ? null
            : JsonSerializer.Deserialize<ProcessIdentity>(
                json,
                SerializerOptions);
    }

    private async ValueTask<SystemTweakRuntimeResult> ExecuteAsync<TState>(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
        where TState : notnull
    {
        TransactionCoordinator<TState> coordinator = new(_journal);
        ActionExecutionResult result = await coordinator.ExecuteAsync(
                action,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        bool succeeded = result.Status is
                ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted;
        if (succeeded)
        {
            SessionCheckpointWriter checkpointWriter = new(_journal);
            _ = await checkpointWriter.RecordAsync(
                    context.SessionId,
                    SessionCheckpoint.SessionActivated,
                    "System Optimizer candidate is active.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return succeeded
            ? SystemTweakRuntimeResult.Success(
                "Kandydat został zastosowany i potwierdzony niezależnym readbackiem.")
            : SystemTweakRuntimeResult.Blocked(
                result.Details ?? $"Operacja zakończyła się stanem {result.Status}.");
    }

    private async ValueTask<SystemTweakRuntimeResult> RecoverAsync<TState>(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
        where TState : notnull
    {
        ActionRecoveryCoordinator<TState> coordinator = new(_journal);
        ActionRecoveryResult result = await coordinator.RecoverAsync(
                action,
                context,
                cancellationToken)
            .ConfigureAwait(false);
        bool succeeded = result.Status is
                ActionRecoveryStatus.Restored
                or ActionRecoveryStatus.AlreadyRestored
                or ActionRecoveryStatus.NotRequired;
        if (succeeded)
        {
            SessionCheckpointWriter checkpointWriter = new(_journal);
            _ = await checkpointWriter.RecordAsync(
                    context.SessionId,
                    SessionCheckpoint.ReconciliationComplete,
                    "System Optimizer candidate was reconciled.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return succeeded
            ? SystemTweakRuntimeResult.Success(
                "Stan original został przywrócony albo cel już był w stanie bazowym.")
            : new(
                Succeeded: false,
                HasConflict:
                    result.Status
                    == ActionRecoveryStatus.ConflictRequiresDecision,
                result.Details ?? $"Recovery zakończyło się stanem {result.Status}.");
    }

    private static ActionExecutionContext CreateContext(
        Guid experimentId,
        int applicationIndex) =>
        new(
            new SessionId(experimentId),
            CreateActionId(experimentId, applicationIndex),
            new IdempotencyKey(CreateDeterministicGuid(
                experimentId,
                $"idempotency:{applicationIndex}")),
            DateTimeOffset.UtcNow);

    private static ActionId CreateActionId(
        Guid experimentId,
        int applicationIndex) =>
        new(CreateDeterministicGuid(
            experimentId,
            $"action:{applicationIndex}"));

    private static Guid CreateDeterministicGuid(
        Guid experimentId,
        string purpose)
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            $"GameShift.SystemOptimizer.v1|{experimentId:D}|{purpose}");
        byte[] hash = SHA256.HashData(payload);
        return new Guid(hash.AsSpan(0, 16));
    }
}

internal sealed record SystemTweakRuntimeResult(
    bool Succeeded,
    bool HasConflict,
    string Message)
{
    internal static SystemTweakRuntimeResult Success(string message) =>
        new(true, HasConflict: false, message);

    internal static SystemTweakRuntimeResult Blocked(string message) =>
        new(false, HasConflict: false, message);
}

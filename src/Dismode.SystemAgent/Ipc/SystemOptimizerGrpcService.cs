using System.Security.Cryptography;
using System.Text.Json;
using Dismode.Contracts.Commands;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.Protocol;
using Dismode.Contracts.SystemOptimization;
using Dismode.Data.SystemOptimization;
using Dismode.SystemAgent.Security;
using Dismode.SystemAgent.SystemOptimization;
using Google.Protobuf;
using Grpc.Core;
using BenchmarkVariant = Dismode.Contracts.SystemOptimization.BenchmarkVariant;
using ExperimentState = Dismode.Contracts.SystemOptimization.ExperimentState;
using RestartRequirement = Dismode.Contracts.SystemOptimization.RestartRequirement;
using SystemTweakAvailability = Dismode.Contracts.SystemOptimization.SystemTweakAvailability;
using SystemTweakRisk = Dismode.Contracts.SystemOptimization.SystemTweakRisk;
using SystemTweakScope = Dismode.Contracts.SystemOptimization.SystemTweakScope;

namespace Dismode.SystemAgent.Ipc;

internal sealed class SystemOptimizerGrpcService :
    DismodeSystemOptimizer.DismodeSystemOptimizerBase,
    IDisposable
{
    private const int MaximumSelections = 64;
    private const int MaximumCaptureFrames = 120_000;
    private const int MaximumHistoryItems = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISystemOptimizerGrpcAuthorization _authorization;
    private readonly SystemOptimizerCoordinator _coordinator;
    private readonly SqliteSystemOptimizerStore _store;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private bool _disposed;

    internal SystemOptimizerGrpcService(
        ISystemOptimizerGrpcAuthorization authorization,
        SystemOptimizerCoordinator coordinator,
        SqliteSystemOptimizerStore store)
    {
        _authorization = authorization;
        _coordinator = coordinator;
        _store = store;
    }

    public override async Task<SystemOptimizerStatusReply> GetStatus(
        SystemOptimizerStatusRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.GetSystemOptimizerStatus,
                        requiresMutation: false,
                        context)
                    .ConfigureAwait(false);
            SystemOptimizerStatus status = await _coordinator.GetStatusAsync(
                    authorized.Caller.ActualUserSid,
                    authorized.Authorization.IsReadOnly,
                    context.CancellationToken)
                .ConfigureAwait(false);
            return ToRpc(status);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerHardwareReply> GetHardware(
        SystemOptimizerHardwareRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            _ = await _authorization.AuthorizeAsync(
                    request.Metadata,
                    request.CalculateSize(),
                    CommandKind.GetHardwareFingerprint,
                    requiresMutation: false,
                    context)
                .ConfigureAwait(false);
            return ToRpc(_coordinator.GetHardware());
        }).ConfigureAwait(false);

    public override async Task<SystemTweakCatalogReply> GetCatalog(
        SystemTweakCatalogRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            _ = await _authorization.AuthorizeAsync(
                    request.Metadata,
                    request.CalculateSize(),
                    CommandKind.GetSystemTweakCatalog,
                    requiresMutation: false,
                    context)
                .ConfigureAwait(false);
            SystemTweakCatalogReply reply = new();
            reply.Definitions.AddRange(
                SystemOptimizerCoordinator.GetCatalog().Select(ToRpc));
            return reply;
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply> SetConsent(
        SetSystemOptimizerConsentRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.SetSystemOptimizerConsent,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.SetSystemOptimizerConsent),
                    ComputePayloadHash(request.Accepted),
                    async () =>
                    {
                        await _coordinator.SetConsentAsync(
                                authorized.Caller.ActualUserSid,
                                request.Accepted,
                                context.CancellationToken)
                            .ConfigureAwait(false);
                        return SuccessOperation(
                            request.Accepted
                                ? "System Optimizer został świadomie włączony."
                                : "Zgoda została wyłączona.");
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemExperimentReply> PrepareExperiment(
        PrepareSystemExperimentRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.PrepareSystemExperiment,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            TweakSelection selection = FromRpc(request.Selection);
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.PrepareSystemExperiment),
                    ComputePayloadHash(
                        request.GameProfileId,
                        request.GameExecutableSha256,
                        request.HardwareFingerprintHash,
                        selection),
                    async () =>
                    {
                        ExperimentPlan experiment =
                            await _coordinator.PrepareExperimentAsync(
                                    authorized.Caller.ActualUserSid,
                                    request.GameProfileId,
                                    request.GameExecutableSha256,
                                    request.HardwareFingerprintHash,
                                    selection,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return ToRpc(
                            experiment,
                            "Eksperyment przygotowany; wejdź do powtarzalnej sceny.");
                    },
                    SystemExperimentReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemExperimentReply> AdvanceExperiment(
        AdvanceSystemExperimentRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.AdvanceSystemExperiment,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            Guid experimentId = ParseGuid(
                request.ExperimentId,
                nameof(request.ExperimentId));
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.AdvanceSystemExperiment),
                    ComputePayloadHash(experimentId, (int)request.Action),
                    async () =>
                    {
                        CoordinatorExperimentResult result =
                            await _coordinator.AdvanceExperimentAsync(
                                    authorized.Caller.ActualUserSid,
                                    experimentId,
                                    request.Action,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return ToRpc(result.Experiment, result.Message);
                    },
                    SystemExperimentReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<BenchmarkDecisionReply> SubmitCapture(
        SubmitBenchmarkCaptureRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.SubmitBenchmarkCapture,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            BenchmarkCapture capture = ParseCapture(request);
            Guid experimentId = ParseGuid(
                request.ExperimentId,
                nameof(request.ExperimentId));
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.SubmitBenchmarkCapture),
                    ComputePayloadHash(
                        experimentId,
                        capture.CaptureId,
                        capture.Variant,
                        capture.StartedAtUtc,
                        capture.StabilizationDuration,
                        capture.MeasurementDuration,
                        capture.FrameTimesMilliseconds,
                        capture.AverageCpuPercent,
                        capture.AverageGpuBusyPercent,
                        capture.AverageUsedRamBytes,
                        capture.ProfileStateHash),
                    async () =>
                    {
                        CoordinatorBenchmarkResult result =
                            await _coordinator.SubmitCaptureAsync(
                                    authorized.Caller.ActualUserSid,
                                    experimentId,
                                    capture,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return ToRpc(result.Experiment, result.Decision);
                    },
                    BenchmarkDecisionReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply> SavePerGameProfile(
        SavePerGameOptimizationProfileRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.SavePerGameOptimizationProfile,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            TweakSelection[] selections = ParseSelections(request.Selections);
            Guid profileId = ParseGuid(request.ProfileId, nameof(request.ProfileId));
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.SavePerGameOptimizationProfile),
                    ComputePayloadHash(
                        profileId,
                        request.GameProfileId,
                        request.HardwareFingerprintHash,
                        selections,
                        request.Enabled),
                    async () =>
                    {
                        PerGameOptimizationProfile profile = new(
                            profileId,
                            authorized.Caller.ActualUserSid,
                            request.GameProfileId,
                            request.HardwareFingerprintHash,
                            selections,
                            request.Enabled,
                            DateTimeOffset.UtcNow);
                        await _coordinator.SavePerGameProfileAsync(
                                profile,
                                context.CancellationToken)
                            .ConfigureAwait(false);
                        return SuccessOperation("Profil gry został zapisany.");
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply> SaveGlobalProfile(
        SaveGlobalOptimizationProfileRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.SaveGlobalOptimizationProfile,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            TweakSelection[] selections = ParseSelections(request.Selections);
            Guid profileId = ParseGuid(request.ProfileId, nameof(request.ProfileId));
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.SaveGlobalOptimizationProfile),
                    ComputePayloadHash(
                        profileId,
                        request.HardwareFingerprintHash,
                        selections,
                        request.Enabled),
                    async () =>
                    {
                        GlobalOptimizationProfile profile = new(
                            profileId,
                            authorized.Caller.ActualUserSid,
                            request.HardwareFingerprintHash,
                            selections,
                            request.Enabled,
                            DateTimeOffset.UtcNow);
                        await _coordinator.SaveGlobalDraftAsync(
                                profile,
                                context.CancellationToken)
                            .ConfigureAwait(false);
                        return SuccessOperation("Szkic profilu globalnego został zapisany.");
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizationProfilesReply> GetProfiles(
        GetSystemOptimizationProfilesRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync<SystemOptimizationProfilesReply>(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.GetSystemOptimizationProfiles,
                        requiresMutation: false,
                        context)
                    .ConfigureAwait(false);
            bool requestsCompleteList =
                string.IsNullOrWhiteSpace(request.GameProfileId);
            PerGameOptimizationProfile? game = requestsCompleteList
                ? null
                : await _store.GetPerGameProfileAsync(
                        authorized.Caller.ActualUserSid,
                        request.GameProfileId,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            IReadOnlyList<PerGameOptimizationProfile> games =
                requestsCompleteList
                    ? await _store.ListPerGameProfilesAsync(
                            authorized.Caller.ActualUserSid,
                            context.CancellationToken)
                        .ConfigureAwait(false)
                    : game is null
                        ? []
                        : [game];
            GlobalOptimizationProfile? global =
                await _store.GetActiveGlobalProfileAsync(
                        context.CancellationToken)
                    .ConfigureAwait(false);
            if (global is not null
                && !StringComparer.OrdinalIgnoreCase.Equals(
                    global.OwnerSid,
                    authorized.Caller.ActualUserSid))
            {
                global = null;
            }

            SystemOptimizationProfilesReply reply = new()
            {
                PerGameProfileJson = game is null
                    ? string.Empty
                    : JsonSerializer.Serialize(game, JsonOptions),
                GlobalProfileJson = global is null
                    ? string.Empty
                    : JsonSerializer.Serialize(global, JsonOptions),
            };
            reply.PerGameProfilesJson.AddRange(
                games.Select(profile =>
                    JsonSerializer.Serialize(profile, JsonOptions)));
            return reply;
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply>
        ActivatePerGameProfile(
            ActivatePerGameOptimizationProfileRequest request,
            ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.ActivatePerGameOptimizationProfile,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.ActivatePerGameOptimizationProfile),
                    ComputePayloadHash(
                        request.GameProfileId,
                        request.GameExecutableSha256),
                    async () =>
                    {
                        SystemOptimizerOperationResult result =
                            await _coordinator.ActivatePerGameProfileAsync(
                                    authorized.Caller.ActualUserSid,
                                    request.GameProfileId,
                                    request.GameExecutableSha256,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return ToRpc(result);
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply>
        RestorePerGameProfile(
            RestorePerGameOptimizationProfileRequest request,
            ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.RestorePerGameOptimizationProfile,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.RestorePerGameOptimizationProfile),
                    ComputePayloadHash("restore-active-game-profile"),
                    async () =>
                    {
                        SystemOptimizerOperationResult result =
                            await _coordinator.RestoreActiveGameProfileAsync(
                                    authorized.Caller.ActualUserSid,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return ToRpc(result);
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply> Restore(
        RestoreSystemOptimizationRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.RestoreSystemOptimization,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            bool restoreExperiment = request.Target is
                SystemRestoreTarget.ActiveExperiment
                or SystemRestoreTarget.AllDismodeChanges;
            bool restoreGlobal = request.Target is
                SystemRestoreTarget.GlobalProfile
                or SystemRestoreTarget.AllDismodeChanges;
            if (!restoreExperiment && !restoreGlobal)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.Target,
                    "Restore target is required.");
            }

            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.RestoreSystemOptimization),
                    ComputePayloadHash((int)request.Target),
                    async () =>
                    {
                        SystemOptimizerOperationResult result =
                            await _coordinator.RestoreAsync(
                                    authorized.Caller.ActualUserSid,
                                    restoreExperiment,
                                    restoreGlobal,
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return new()
                        {
                            Succeeded = result.Succeeded,
                            ReadOnly = false,
                            RecoveryRequired = result.HasConflict,
                            Message = result.Message,
                        };
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizationHistoryReply> GetHistory(
        SystemOptimizationHistoryRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.GetSystemOptimizationHistory,
                        requiresMutation: false,
                        context)
                    .ConfigureAwait(false);
            if (request.MaximumItems is <= 0 or > MaximumHistoryItems)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.MaximumItems,
                    $"History size must be between 1 and {MaximumHistoryItems}.");
            }

            IReadOnlyList<SystemOptimizationHistoryRecord> entries =
                await _store.ListHistoryAsync(
                        authorized.Caller.ActualUserSid,
                        request.MaximumItems,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            SystemOptimizationHistoryReply reply = new();
            reply.Entries.AddRange(entries.Select(ToRpc));
            return reply;
        }).ConfigureAwait(false);

    public override async Task<SystemOptimizerOperationReply> PrepareForUpdate(
        PrepareSystemOptimizerUpdateRequest request,
        ServerCallContext context) =>
        await ExecuteRpcAsync(async () =>
        {
            AuthorizedSystemOptimizerRequest authorized =
                await _authorization.AuthorizeAsync(
                        request.Metadata,
                        request.CalculateSize(),
                        CommandKind.PrepareSystemOptimizerUpdate,
                        requiresMutation: true,
                        context)
                    .ConfigureAwait(false);
            return await ExecuteIdempotentAsync(
                    authorized,
                    nameof(CommandKind.PrepareSystemOptimizerUpdate),
                    ComputePayloadHash("prepare-update"),
                    async () =>
                    {
                        SystemOptimizerOperationResult result =
                            await _coordinator.PrepareForUpdateAsync(
                                    context.CancellationToken)
                                .ConfigureAwait(false);
                        return new()
                        {
                            Succeeded = result.Succeeded,
                            ReadOnly = false,
                            RecoveryRequired = result.HasConflict,
                            Message = result.Message,
                        };
                    },
                    SystemOptimizerOperationReply.Parser,
                    context.CancellationToken)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mutationGate.Dispose();
        _disposed = true;
    }

    private async ValueTask<TReply> ExecuteIdempotentAsync<TReply>(
        AuthorizedSystemOptimizerRequest authorized,
        string commandName,
        string requestSha256,
        Func<ValueTask<TReply>> action,
        MessageParser<TReply> parser,
        CancellationToken cancellationToken)
        where TReply : class, IMessage<TReply>
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? existing = await _store.GetIdempotentResponseAsync(
                    authorized.Caller.ActualUserSid,
                    authorized.Metadata.IdempotencyKey.Value,
                    commandName,
                    requestSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return parser.ParseJson(existing);
            }

            TReply reply = await action().ConfigureAwait(false);
            string responseJson = JsonFormatter.Default.Format(reply);
            await _store.SaveIdempotentResponseAsync(
                    authorized.Caller.ActualUserSid,
                    authorized.Metadata.IdempotencyKey.Value,
                    commandName,
                    requestSha256,
                    responseJson,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return reply;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static BenchmarkCapture ParseCapture(
        SubmitBenchmarkCaptureRequest request)
    {
        if (request.FrameTimesMilliseconds.Count > MaximumCaptureFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.FrameTimesMilliseconds.Count,
                $"Capture contains more than {MaximumCaptureFrames} frames.");
        }

        if (request.StabilizationMilliseconds is < 0 or > 60_000
            || request.MeasurementMilliseconds is < 0 or > 120_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Capture durations exceed protocol bounds.");
        }

        DateTimeOffset started;
        try
        {
            started = DateTimeOffset.FromUnixTimeMilliseconds(
                request.StartedUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException(
                "Capture start timestamp is invalid.",
                exception);
        }

        ValidatePercentage(request.HasAverageCpuPercent
            ? request.AverageCpuPercent
            : null);
        ValidatePercentage(request.HasAverageGpuBusyPercent
            ? request.AverageGpuBusyPercent
            : null);
        if (request.HasAverageUsedRamBytes
            && request.AverageUsedRamBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request));
        }

        return new(
            ParseGuid(request.CaptureId, nameof(request.CaptureId)),
            (BenchmarkVariant)(int)request.Variant,
            started,
            TimeSpan.FromMilliseconds(request.StabilizationMilliseconds),
            TimeSpan.FromMilliseconds(request.MeasurementMilliseconds),
            request.FrameTimesMilliseconds.ToArray(),
            request.HasAverageCpuPercent ? request.AverageCpuPercent : null,
            request.HasAverageGpuBusyPercent
                ? request.AverageGpuBusyPercent
                : null,
            request.HasAverageUsedRamBytes
                ? request.AverageUsedRamBytes
                : null,
            request.ProfileStateHash);
    }

    private static TweakSelection[] ParseSelections(
        IEnumerable<SystemTweakSelectionMessage> selections)
    {
        TweakSelection[] parsed = selections.Select(FromRpc).ToArray();
        if (parsed.Length is 0 or > MaximumSelections)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selections),
                parsed.Length,
                $"Selection count must be between 1 and {MaximumSelections}.");
        }

        return parsed;
    }

    private static TweakSelection FromRpc(
        SystemTweakSelectionMessage? selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return new(
            selection.TweakId,
            selection.Revision,
            selection.Value,
            string.IsNullOrWhiteSpace(selection.TargetId)
                ? null
                : selection.TargetId,
            string.IsNullOrWhiteSpace(selection.DangerousConfirmation)
                ? null
                : selection.DangerousConfirmation);
    }

    private static SystemTweakSelectionMessage ToRpc(
        TweakSelection selection) =>
        new()
        {
            TweakId = selection.TweakId,
            Revision = selection.Revision,
            Value = selection.Value,
            TargetId = selection.TargetId ?? string.Empty,
            DangerousConfirmation =
                selection.DangerousConfirmation ?? string.Empty,
        };

    private static SystemOptimizerStatusReply ToRpc(
        SystemOptimizerStatus status)
    {
        RecoveryStatusMessage recovery = new()
        {
            Phase = (int)status.Recovery.Phase,
            ExperimentId = status.Recovery.ExperimentId?.ToString("D")
                ?? string.Empty,
            PhaseEnteredUnixMilliseconds =
                status.Recovery.PhaseEnteredAtUtc?.ToUnixTimeMilliseconds()
                ?? 0,
            IsJournalClean = status.Recovery.IsJournalClean,
            HasConflicts = status.Recovery.HasConflicts,
            Message = status.Recovery.Message,
        };
        recovery.ConflictTargets.AddRange(status.Recovery.ConflictTargets);
        SystemOptimizerStatusReply reply = new()
        {
            IsServiceReady = status.IsServiceReady,
            IsReadOnly = status.IsReadOnly,
            HasUserConsent = status.HasUserConsent,
            ActiveExperimentId = status.ActiveExperimentId?.ToString("D")
                ?? string.Empty,
            ActiveGlobalProfileId =
                status.ActiveGlobalProfileId?.ToString("D") ?? string.Empty,
            ActiveGameProfileId = status.ActiveGameProfileId ?? string.Empty,
            Recovery = recovery,
            Message = status.Message,
            ObservedUnixMilliseconds =
                status.ObservedAtUtc.ToUnixTimeMilliseconds(),
            ProtocolVersion = ProtocolInfo.CurrentVersion,
        };
        if (status.ActiveExperiment is not null)
        {
            reply.ActiveExperiment = ToRpc(
                status.ActiveExperiment,
                status.ActiveExperiment.State.ToString());
        }

        return reply;
    }

    private static SystemOptimizerHardwareReply ToRpc(
        HardwareFingerprint hardware)
    {
        SystemOptimizerHardwareReply reply = new()
        {
            SchemaVersion = hardware.SchemaVersion,
            WindowsBuild = hardware.WindowsBuild,
            CpuArchitecture = hardware.CpuArchitecture,
            CpuVendor = hardware.CpuVendor,
            CpuModelFamily = hardware.CpuModelFamily,
            PhysicalMemoryBytes = hardware.PhysicalMemoryBytes,
            IsLaptop = hardware.IsLaptop,
            FingerprintHash = hardware.FingerprintHash,
        };
        reply.GraphicsAdapters.AddRange(hardware.GraphicsAdapters);
        reply.GraphicsDriverVersions.AddRange(
            hardware.GraphicsDriverVersions);
        reply.NetworkAdapterClasses.AddRange(hardware.NetworkAdapterClasses);
        return reply;
    }

    private static SystemTweakDefinitionMessage ToRpc(
        TweakDefinition definition)
    {
        SystemTweakDefinitionMessage reply = new()
        {
            Id = definition.Id,
            Revision = definition.Revision,
            DisplayName = definition.DisplayName,
            Category = definition.Category,
            Description = definition.Description,
            Risk = (SystemTweakRiskValue)(int)definition.Risk,
            Scope = (SystemTweakScopeValue)(int)definition.Scope,
            RestartRequirement =
                (RestartRequirementValue)(int)definition.RestartRequirement,
            Availability =
                (SystemTweakAvailabilityValue)(int)definition.Availability,
            TechnicalSource = definition.TechnicalSource,
            RestoreDescription = definition.RestoreDescription,
            BlockingReason = definition.BlockingReason ?? string.Empty,
            MinimumWindowsBuild = definition.MinimumWindowsBuild,
            RequiresTarget = definition.RequiresTarget,
        };
        if (definition.MaximumWindowsBuild is int maximumBuild)
        {
            reply.MaximumWindowsBuild = maximumBuild;
        }

        reply.AllowedValues.AddRange(definition.AllowedValues);
        reply.ConflictsWith.AddRange(definition.ConflictsWith);
        return reply;
    }

    private static SystemExperimentReply ToRpc(
        ExperimentPlan experiment,
        string message) =>
        new()
        {
            ExperimentId = experiment.ExperimentId.ToString("D"),
            OwnerSid = experiment.OwnerSid,
            GameProfileId = experiment.GameProfileId,
            GameExecutableSha256 = experiment.GameExecutableHash,
            HardwareFingerprintHash = experiment.HardwareFingerprintHash,
            Selection = ToRpc(experiment.Selection),
            FirstVariant =
                (BenchmarkVariantValue)(int)experiment.FirstVariant,
            State = (ExperimentStateValue)(int)experiment.State,
            RequiredPairs = experiment.RequiredPairs,
            CompletedBaselineCaptures =
                experiment.CompletedBaselineCaptures,
            CompletedCandidateCaptures =
                experiment.CompletedCandidateCaptures,
            CreatedUnixMilliseconds =
                experiment.CreatedAtUtc.ToUnixTimeMilliseconds(),
            UpdatedUnixMilliseconds =
                experiment.UpdatedAtUtc.ToUnixTimeMilliseconds(),
            Message = message,
        };

    private static BenchmarkDecisionReply ToRpc(
        ExperimentPlan experiment,
        Dismode.Core.SystemOptimization.BenchmarkDecision decision) =>
        new()
        {
            ExperimentId = experiment.ExperimentId.ToString("D"),
            Verdict = (int)decision.Verdict,
            RequiresAdditionalPair = decision.RequiresAdditionalPair,
            PrimaryMetricImprovementPercent =
                decision.PrimaryMetricImprovementPercent,
            AverageFpsChangePercent =
                decision.AverageFramesPerSecondChangePercent,
            BaselineHitchRatePercent = decision.BaselineHitchRatePercent,
            CandidateHitchRatePercent = decision.CandidateHitchRatePercent,
            CompletedPairs = decision.CompletedPairs,
            Explanation = decision.Explanation,
            Experiment = ToRpc(experiment, decision.Explanation),
        };

    private static SystemOptimizationHistoryEntry ToRpc(
        SystemOptimizationHistoryRecord entry) =>
        new()
        {
            Sequence = entry.Sequence,
            OwnerSid = entry.OwnerSid,
            OperationKind = entry.OperationKind,
            TargetId = entry.TargetId ?? string.Empty,
            Success = entry.Success,
            Details = entry.Details,
            CreatedUnixMilliseconds =
                entry.CreatedAtUtc.ToUnixTimeMilliseconds(),
        };

    private static SystemOptimizerOperationReply SuccessOperation(
        string message) =>
        new()
        {
            Succeeded = true,
            ReadOnly = false,
            RecoveryRequired = false,
            Message = message,
        };

    private static SystemOptimizerOperationReply ToRpc(
        SystemOptimizerOperationResult result) =>
        new()
        {
            Succeeded = result.Succeeded,
            ReadOnly = false,
            RecoveryRequired = result.HasConflict,
            Message = result.Message,
        };

    private static string ComputePayloadHash(params object?[] values)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            values,
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static Guid ParseGuid(string value, string parameterName) =>
        Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException(
                "A non-empty GUID is required.",
                parameterName);

    private static void ValidatePercentage(double? value)
    {
        if (value is double number
            && (!double.IsFinite(number) || number is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static async Task<T> ExecuteRpcAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new RpcException(
                new Status(StatusCode.PermissionDenied, exception.Message));
        }
        catch (KeyNotFoundException exception)
        {
            throw new RpcException(
                new Status(StatusCode.NotFound, exception.Message));
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, exception.Message));
        }
        catch (InvalidDataException exception)
        {
            throw new RpcException(
                new Status(StatusCode.DataLoss, exception.Message));
        }
        catch (InvalidOperationException exception)
        {
            throw new RpcException(
                new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }
}

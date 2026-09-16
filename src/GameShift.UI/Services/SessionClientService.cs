using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Windows.Ipc;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameShift.UI.Services;

public sealed class SessionClientService : IDisposable
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PrepareTimeout =
        TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StartTimeout =
        TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RestoreTimeout =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CloseGameTimeout =
        TimeSpan.FromSeconds(60);

    private readonly string _callerSid;
    private readonly GrpcChannel _channel;
    private readonly GameShiftSessions.GameShiftSessionsClient _client;
    private bool _disposed;

    public SessionClientService(string callerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        _callerSid = callerSid.Trim();
        _channel = NamedPipeGrpcChannelFactory.Create(
            PipeNames.ForUser(_callerSid));
        _client = new(_channel);
    }

    public async Task<SessionPlanClientSnapshot> PrepareAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        return await PrepareAsync(
                profileId,
                backgroundApplications: [],
                GamePriorityClientMode.Normal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionPlanClientSnapshot> PrepareAsync(
        Guid profileId,
        IReadOnlyList<BackgroundApplicationClientSelection>
            backgroundApplications,
        CancellationToken cancellationToken)
    {
        return await PrepareAsync(
                profileId,
                backgroundApplications,
                GamePriorityClientMode.Normal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SessionPlanClientSnapshot> PrepareAsync(
        Guid profileId,
        IReadOnlyList<BackgroundApplicationClientSelection>
            backgroundApplications,
        GamePriorityClientMode gamePriority,
        CancellationToken cancellationToken,
        bool useSavedBackgroundRules = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(backgroundApplications);
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException(
                "Identyfikator profilu nie może być pusty.",
                nameof(profileId));
        }

        PrepareSessionRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.StartOptimizationSession,
                sessionId: null),
            ProfileId = profileId.ToString("D"),
            UseSavedBackgroundRules = useSavedBackgroundRules,
            GamePriority = gamePriority switch
            {
                GamePriorityClientMode.Normal =>
                    GamePriorityMode.Normal,
                GamePriorityClientMode.AboveNormal =>
                    GamePriorityMode.AboveNormal,
                GamePriorityClientMode.High =>
                    GamePriorityMode.High,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(gamePriority),
                    gamePriority,
                    "Nieobsługiwany priorytet gry."),
            },
        };
        request.BackgroundApplications.AddRange(
            backgroundApplications.Select(application =>
                new BackgroundApplicationSelection
                {
                    ProcessId = application.ProcessId,
                    StartedUnixMilliseconds =
                        application.StartedAtUtc
                            .ToUniversalTime()
                            .ToUnixTimeMilliseconds(),
                    ActionMode = application.ActionMode switch
                    {
                        BackgroundProcessClientActionMode
                            .CloseAndRestore =>
                            BackgroundProcessActionMode
                                .CloseAndRestore,
                        BackgroundProcessClientActionMode
                            .LowerPriority =>
                            BackgroundProcessActionMode
                                .LowerPriority,
                        BackgroundProcessClientActionMode
                            .LowerPriorityAndEcoQos =>
                            BackgroundProcessActionMode
                                .LowerPriorityAndEcoQos,
                        BackgroundProcessClientActionMode
                            .RestrainBackground =>
                            BackgroundProcessActionMode
                                .RestrainBackground,
                        _ => throw new ArgumentOutOfRangeException(
                            nameof(backgroundApplications),
                            application.ActionMode,
                            "Nieobsługiwany sposób optymalizacji tła."),
                    },
                }));
        SessionPlanReply reply = await ExecuteAsync(
            token => _client.PrepareSessionAsync(
                request,
                cancellationToken: token),
            cancellationToken,
            PrepareTimeout);

        return new(
            ParseRequiredGuid(reply.PlanId, "plan_id"),
            ParseRequiredGuid(reply.SessionId, "session_id"),
            ParseRequiredGuid(reply.ProfileId, "profile_id"),
            RequireText(reply.GameDisplayName, "game_display_name"),
            ParseTimestamp(
                reply.ExpiresUnixMilliseconds,
                "expires_unix_milliseconds"),
            reply.Actions
                .Select(action =>
                    new SessionPlanActionClientSnapshot(
                        action.Code,
                        action.Description,
                        action.Risk,
                        action.Recovery))
                .ToArray(),
            reply.SystemMutationsEnabled,
            reply.SafetyMessage);
    }

    public async Task<SessionStateClientSnapshot> StartAsync(
        Guid planId,
        Guid sessionId,
        CancellationToken cancellationToken,
        bool enableFrameRateTracking = true,
        bool enableProBalance = false,
        bool attachOnly = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StartSessionRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.ApproveOptimizationPlan,
                sessionId),
            PlanId = RequireGuid(planId, nameof(planId)).ToString("D"),
            EnableFrameRateTracking = enableFrameRateTracking,
            EnableProBalance = enableProBalance,
            AttachOnly = attachOnly,
        };
        SessionStateReply reply = await ExecuteAsync(
            token => _client.StartSessionAsync(
                request,
                cancellationToken: token),
            cancellationToken,
            StartTimeout);
        return MapRequiredState(reply);
    }

    public async Task<SessionStateClientSnapshot> SetFrameRateTrackingAsync(
        Guid sessionId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetFrameRateTrackingRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.SetFrameRateTracking,
                RequireGuid(sessionId, nameof(sessionId))),
            Enabled = enabled,
        };
        SessionStateReply reply = await ExecuteAsync(
            token => _client.SetFrameRateTrackingAsync(
                request,
                cancellationToken: token),
            cancellationToken);
        return MapRequiredState(reply);
    }

    public async Task<SessionStateClientSnapshot?> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GetActiveSessionRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.GetActiveSession,
                sessionId: null),
        };
        SessionStateReply reply = await ExecuteAsync(
            token => _client.GetActiveSessionAsync(
                request,
                cancellationToken: token),
            cancellationToken);
        return reply.HasSession ? MapRequiredState(reply) : null;
    }

    public async Task<SessionShutdownReadinessClientSnapshot>
        GetShutdownReadinessAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShutdownReadinessRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.ShutdownComponents,
                sessionId: null),
        };
        ShutdownReadinessReply reply = await ExecuteAsync(
            token => _client.GetShutdownReadinessAsync(
                request,
                cancellationToken: token),
            cancellationToken);
        return new(
            reply.CanShutdown,
            reply.HasActiveSession,
            reply.HasPreparedPlan,
            RequireText(reply.Message, "message"));
    }

    public async Task<SessionShutdownReadinessClientSnapshot>
        ShutdownComponentsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShutdownComponentsRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.ShutdownComponents,
                sessionId: null),
        };
        ShutdownReadinessReply reply = await ExecuteAsync(
            token => _client.ShutdownComponentsAsync(
                request,
                cancellationToken: token),
            cancellationToken);
        return new(
            reply.CanShutdown,
            reply.HasActiveSession,
            reply.HasPreparedPlan,
            RequireText(reply.Message, "message"));
    }

    public async Task<SessionStateClientSnapshot> RestoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RestoreSessionRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.RestoreOptimizationSession,
                sessionId),
        };
        SessionStateReply reply = await ExecuteAsync(
            token => _client.RestoreSessionAsync(
                request,
                cancellationToken: token),
            cancellationToken,
            RestoreTimeout);
        return MapRequiredState(reply);
    }

    public async Task<SessionStateClientSnapshot> CloseGameAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CloseGameRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.CloseGame,
                sessionId),
            ForceTermination = true,
        };
        SessionStateReply reply = await ExecuteAsync(
            token => _client.CloseGameAsync(
                request,
                cancellationToken: token),
            cancellationToken,
            CloseGameTimeout);
        return MapRequiredState(reply);
    }

    private static async Task<TReply> ExecuteAsync<TReply>(
        Func<CancellationToken, AsyncUnaryCall<TReply>> operation,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(requestTimeout ?? RequestTimeout);

        try
        {
            using AsyncUnaryCall<TReply> call = operation(timeout.Token);
            return await call.ResponseAsync.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "SessionHost nie odpowiedział w wymaganym czasie.");
        }
        catch (RpcException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Żądanie sesji zostało anulowane.",
                exception,
                cancellationToken);
        }
        catch (RpcException exception)
        {
            throw TranslateRpcFailure(exception);
        }
        catch (HttpRequestException exception)
        {
            throw new IOException(
                "Nie można połączyć się z SessionHost.",
                exception);
        }
    }

    private RpcRequestMetadata CreateMetadata(
        CommandKind command,
        Guid? sessionId)
    {
        RequestMetadata metadata = new(
            ProtocolInfo.CurrentVersion,
            Guid.NewGuid(),
            sessionId,
            _callerSid,
            DateTimeOffset.UtcNow,
            command,
            RandomNumberGenerator.GetHexString(32),
            IdempotencyKey.Create());
        return RpcRequestMetadataMapper.ToRpc(metadata);
    }

    private static SessionStateClientSnapshot MapRequiredState(
        SessionStateReply reply)
    {
        if (!reply.HasSession)
        {
            throw new InvalidDataException(
                "SessionHost nie zwrócił oczekiwanego stanu sesji.");
        }

        return new(
            ParseRequiredGuid(reply.SessionId, "session_id"),
            ParseRequiredGuid(reply.ProfileId, "profile_id"),
            RequireText(reply.GameDisplayName, "game_display_name"),
            RequireText(reply.State, "state"),
            ParseTimestamp(
                reply.StartedUnixMilliseconds,
                "started_unix_milliseconds"),
            reply.AppliedActionCount,
            reply.RestoredActionCount,
            reply.ConflictCount,
            reply.ErrorCount,
            reply.Message,
            reply.HasFramesPerSecond
                ? reply.FramesPerSecond
                : null,
            reply.HasFrameTimeMilliseconds
                ? reply.FrameTimeMilliseconds
                : null,
            reply.FrameRateStatus,
            reply.HasFrameRateProcessId
                ? reply.FrameRateProcessId
                : null);
    }

    private static Exception TranslateRpcFailure(RpcException exception)
    {
        string detail = string.IsNullOrWhiteSpace(exception.Status.Detail)
            ? "Host odrzucił żądanie bez dodatkowych informacji."
            : exception.Status.Detail;

        return exception.StatusCode switch
        {
            StatusCode.Unavailable
                or StatusCode.DeadlineExceeded
                or StatusCode.Cancelled =>
                    new IOException(
                        "SessionHost nie jest dostępny.",
                        exception),
            StatusCode.PermissionDenied
                or StatusCode.Unauthenticated =>
                    new UnauthorizedAccessException(
                        $"SessionHost odrzucił tożsamość żądania: {detail}",
                        exception),
            StatusCode.DataLoss =>
                new InvalidDataException(
                    $"Dane recovery są niespójne: {detail}",
                    exception),
            _ => new InvalidOperationException(
                $"SessionHost odrzucił operację: {detail}",
                exception),
        };
    }

    private static Guid ParseRequiredGuid(
        string value,
        string fieldName)
    {
        if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty)
        {
            throw new InvalidDataException(
                $"SessionHost zwrócił nieprawidłowe pole {fieldName}.");
        }

        return parsed;
    }

    private static Guid RequireGuid(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "Identyfikator nie może być pusty.",
                parameterName);
        }

        return value;
    }

    private static string RequireText(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"SessionHost zwrócił puste pole {fieldName}.");
        }

        return value;
    }

    private static DateTimeOffset ParseTimestamp(
        long unixMilliseconds,
        string fieldName)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException(
                $"SessionHost zwrócił nieprawidłowe pole {fieldName}.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _channel.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

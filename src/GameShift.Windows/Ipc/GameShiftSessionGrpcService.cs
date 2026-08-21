using System.ComponentModel;
using System.Diagnostics;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Ipc;
using GameShift.Windows.Sessions;
using Grpc.Core;
using RpcBackgroundProcessActionMode =
    GameShift.Contracts.Grpc.BackgroundProcessActionMode;
using SessionBackgroundApplicationSelection =
    GameShift.Windows.Sessions.BackgroundApplicationSelection;
using SessionBackgroundProcessActionMode =
    GameShift.Windows.Sessions.BackgroundProcessActionMode;

namespace GameShift.Windows.Ipc;

public sealed class GameShiftSessionGrpcService :
    GameShiftSessions.GameShiftSessionsBase
{
    private readonly GrpcRequestValidator _requestValidator;
    private readonly LocalGameSessionOrchestrator _orchestrator;

    public GameShiftSessionGrpcService(
        RequestValidationPolicy validationPolicy,
        LocalGameSessionOrchestrator orchestrator)
    {
        _requestValidator = new(validationPolicy);
        _orchestrator = orchestrator;
    }

    public override async Task<SessionPlanReply> PrepareSession(
        PrepareSessionRequest request,
        ServerCallContext context)
    {
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.StartOptimizationSession);
        if (request.UseSavedBackgroundRules
            && request.BackgroundApplications.Count > 0)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "Saved rules cannot be combined with explicit process "
                    + "selections."));
        }

        if (request.UseSavedBackgroundRules
            && request.GamePriority is not (
                GamePriorityMode.Unspecified
                or GamePriorityMode.Normal))
        {
            throw new RpcException(
                    new Status(
                        StatusCode.InvalidArgument,
                        "Saved-rule mode keeps the game priority normal. "
                        + "Experimental priority must be selected manually "
                        + "when preparing a plan."));
        }

        GameProfileId profileId = new(
            ParseGuid(request.ProfileId, "profile_id"));
        SessionBackgroundApplicationSelection[] backgroundApplications =
            request.BackgroundApplications
                .Select(selection =>
                    new SessionBackgroundApplicationSelection(
                        selection.ProcessId,
                        ParseTimestamp(
                            selection.StartedUnixMilliseconds,
                            "started_unix_milliseconds"),
                        selection.ActionMode switch
                        {
                            RpcBackgroundProcessActionMode.Unspecified
                                or RpcBackgroundProcessActionMode
                                    .CloseAndRestore =>
                                SessionBackgroundProcessActionMode
                                    .CloseAndRestore,
                            RpcBackgroundProcessActionMode.LowerPriority =>
                                SessionBackgroundProcessActionMode
                                    .LowerPriority,
                            RpcBackgroundProcessActionMode
                                    .LowerPriorityAndEcoQos =>
                                SessionBackgroundProcessActionMode
                                    .LowerPriorityAndEcoQos,
                            _ => throw new RpcException(
                                new Status(
                                    StatusCode.InvalidArgument,
                                    "action_mode is not supported.")),
                        }))
                .ToArray();
        ProcessPriorityClass? gamePriority = request.GamePriority switch
        {
            GamePriorityMode.Unspecified
                or GamePriorityMode.Normal => null,
            GamePriorityMode.AboveNormal =>
                ProcessPriorityClass.AboveNormal,
            GamePriorityMode.High => ProcessPriorityClass.High,
            _ => throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "game_priority is not supported.")),
        };

        try
        {
            SessionPlanPreview plan = await _orchestrator.PrepareAsync(
                profileId,
                backgroundApplications,
                gamePriority,
                context.CancellationToken,
                request.UseSavedBackgroundRules);
            return ToReply(plan);
        }
        catch (Exception exception) when (IsExpectedSessionFailure(exception))
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<SessionStateReply> StartSession(
        StartSessionRequest request,
        ServerCallContext context)
    {
        Guid planId = ParseGuid(request.PlanId, "plan_id");
        SessionId? expectedSessionId =
            await _orchestrator.GetExpectedStartSessionIdAsync(
                planId,
                context.CancellationToken);
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.ApproveOptimizationPlan,
            expectedSessionId?.Value);
        SessionId sessionId = new(
            ParseRequiredSessionId(request.Metadata));

        try
        {
            GameSessionSnapshot snapshot =
                await _orchestrator.StartAsync(
                    planId,
                    sessionId,
                    context.CancellationToken);
            return ToReply(snapshot);
        }
        catch (Exception exception) when (IsExpectedSessionFailure(exception))
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<SessionStateReply> GetActiveSession(
        GetActiveSessionRequest request,
        ServerCallContext context)
    {
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.GetActiveSession);
        GameSessionSnapshot? active =
            await _orchestrator.GetActiveAsync(context.CancellationToken);
        return active is null
            ? EmptyReply("No game session is active.")
            : ToReply(active);
    }

    public override async Task<SessionStateReply> RestoreSession(
        RestoreSessionRequest request,
        ServerCallContext context)
    {
        SessionId? expectedSessionId =
            await _orchestrator.GetActiveSessionIdAsync(
                context.CancellationToken);
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.RestoreOptimizationSession,
            expectedSessionId?.Value);
        SessionId sessionId = new(
            ParseRequiredSessionId(request.Metadata));

        try
        {
            GameSessionSnapshot result =
                await _orchestrator.RestoreAsync(
                    sessionId,
                    context.CancellationToken);
            return ToReply(result);
        }
        catch (Exception exception) when (IsExpectedSessionFailure(exception))
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<SessionStateReply> CloseGame(
        CloseGameRequest request,
        ServerCallContext context)
    {
        SessionId? expectedSessionId =
            await _orchestrator.GetActiveSessionIdAsync(
                context.CancellationToken);
        _requestValidator.Validate(
            request.Metadata,
            request.CalculateSize(),
            CommandKind.CloseGame,
            expectedSessionId?.Value);
        SessionId sessionId = new(
            ParseRequiredSessionId(request.Metadata));

        try
        {
            GameSessionSnapshot result =
                await _orchestrator.CloseGameAsync(
                    sessionId,
                    request.ForceTermination,
                    context.CancellationToken);
            return ToReply(result);
        }
        catch (Exception exception) when (IsExpectedSessionFailure(exception))
        {
            throw ToRpcException(exception);
        }
    }

    private static SessionPlanReply ToReply(SessionPlanPreview plan)
    {
        SessionPlanReply reply = new()
        {
            PlanId = plan.PlanId.ToString("D"),
            SessionId = plan.SessionId.ToString(),
            ProfileId = plan.ProfileId.ToString(),
            GameDisplayName = plan.GameDisplayName,
            ExpiresUnixMilliseconds =
                plan.ExpiresAtUtc.ToUnixTimeMilliseconds(),
            SystemMutationsEnabled = plan.SystemMutationsEnabled,
            SafetyMessage = plan.SafetyMessage,
        };
        reply.Actions.AddRange(
            plan.Items.Select(item =>
                new PlannedSessionAction
                {
                    Code = item.Code,
                    Description = item.Description,
                    Risk = item.Risk,
                    Recovery = item.Recovery,
                }));
        return reply;
    }

    private static SessionStateReply ToReply(
        GameSessionSnapshot snapshot)
    {
        SessionStateReply reply = new()
        {
            HasSession = true,
            SessionId = snapshot.SessionId.ToString(),
            ProfileId = snapshot.ProfileId.ToString(),
            GameDisplayName = snapshot.GameDisplayName,
            State = snapshot.State.ToString(),
            StartedUnixMilliseconds =
                snapshot.StartedAtUtc.ToUnixTimeMilliseconds(),
            AppliedActionCount = snapshot.AppliedActionCount,
            RestoredActionCount = snapshot.RestoredActionCount,
            ConflictCount = snapshot.ConflictCount,
            ErrorCount = snapshot.ErrorCount,
            Message = snapshot.Message,
            FrameRateStatus = snapshot.FrameRateStatus,
        };
        if (snapshot.FramesPerSecond is double framesPerSecond)
        {
            reply.FramesPerSecond = framesPerSecond;
        }

        if (snapshot.FrameTimeMilliseconds is double frameTimeMilliseconds)
        {
            reply.FrameTimeMilliseconds = frameTimeMilliseconds;
        }

        if (snapshot.FrameRateProcessId is int frameRateProcessId)
        {
            reply.FrameRateProcessId = frameRateProcessId;
        }

        return reply;
    }

    private static SessionStateReply EmptyReply(string message) =>
        new()
        {
            HasSession = false,
            State = "Idle",
            Message = message,
        };

    private static Guid ParseRequiredSessionId(
        RpcRequestMetadata? metadata)
    {
        if (metadata is null
            || !Guid.TryParse(metadata.SessionId, out Guid sessionId)
            || sessionId == Guid.Empty)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "A valid session_id is required."));
        }

        return sessionId;
    }

    private static Guid ParseGuid(string value, string fieldName)
    {
        if (!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"{fieldName} must be a non-empty GUID."));
        }

        return parsed;
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
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    $"{fieldName} is outside the supported timestamp range."),
                exception.Message);
        }
    }

    private static bool IsExpectedSessionFailure(Exception exception) =>
        exception is
            ArgumentException
            or InvalidOperationException
            or KeyNotFoundException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or Win32Exception;

    private static RpcException ToRpcException(Exception exception)
    {
        StatusCode statusCode = exception switch
        {
            KeyNotFoundException => StatusCode.NotFound,
            InvalidDataException => StatusCode.DataLoss,
            ArgumentException => StatusCode.InvalidArgument,
            IOException or UnauthorizedAccessException =>
                StatusCode.Aborted,
            _ => StatusCode.FailedPrecondition,
        };

        return new(new Status(statusCode, exception.Message));
    }
}

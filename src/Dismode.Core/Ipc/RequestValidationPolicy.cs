using Dismode.Contracts.Commands;
using Dismode.Contracts.Protocol;

namespace Dismode.Core.Ipc;

public sealed class RequestValidationPolicy
{
    private readonly string _expectedCallerSid;
    private readonly IRequestReplayGuard _replayGuard;
    private readonly TimeProvider _timeProvider;

    public RequestValidationPolicy(
        string expectedCallerSid,
        IRequestReplayGuard replayGuard,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCallerSid);
        _expectedCallerSid = expectedCallerSid.Trim();
        _replayGuard = replayGuard;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RequestValidationResult Validate(
        RequestMetadata request,
        int serializedMessageBytes,
        Guid? expectedSessionId = null)
    {
        if (serializedMessageBytes < 0
            || serializedMessageBytes > ProtocolInfo.MaximumMessageBytes)
        {
            return RequestValidationResult.Rejected(
                RequestValidationFailure.MessageTooLarge,
                "The request exceeds the configured message-size limit.");
        }

        if (request.ProtocolVersion != ProtocolInfo.CurrentVersion)
        {
            return RequestValidationResult.Rejected(
                RequestValidationFailure.UnsupportedProtocolVersion,
                "The protocol version is not supported.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                request.CallerSid,
                _expectedCallerSid))
        {
            return RequestValidationResult.Rejected(
                RequestValidationFailure.CallerSidMismatch,
                "The claimed caller SID does not match the pipe identity.");
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();
        TimeSpan clockDelta = (nowUtc - request.TimestampUtc).Duration();
        if (clockDelta > ProtocolInfo.MaximumClockSkew)
        {
            return RequestValidationResult.Rejected(
                RequestValidationFailure.TimestampOutsideAllowedWindow,
                "The request timestamp is outside the allowed clock window.");
        }

        if (RequiresOwnedSession(request.Command)
            && (request.SessionId is null
                || expectedSessionId is null
                || request.SessionId != expectedSessionId))
        {
            return RequestValidationResult.Rejected(
                RequestValidationFailure.SessionMismatch,
                "The command does not address the caller's active session.");
        }

        ReplayRegistrationResult replay = _replayGuard.TryRegister(
            request.RequestId,
            nowUtc);

        return replay switch
        {
            ReplayRegistrationResult.Registered =>
                RequestValidationResult.Valid(),
            ReplayRegistrationResult.Duplicate =>
                RequestValidationResult.Rejected(
                    RequestValidationFailure.ReplayDetected,
                    "The request ID has already been processed."),
            ReplayRegistrationResult.CapacityExhausted =>
                RequestValidationResult.Rejected(
                    RequestValidationFailure.ReplayCapacityExhausted,
                    "Replay protection is at capacity; retry after the retention window."),
            _ => throw new InvalidOperationException(
                $"Unknown replay registration result: {replay}."),
        };
    }

    private static bool RequiresOwnedSession(CommandKind command) =>
        command is
            CommandKind.ApproveOptimizationPlan
            or CommandKind.RestoreOptimizationSession
            or CommandKind.CloseGame
            or CommandKind.StopApprovedService
            or CommandKind.SetProcessPriority
            or CommandKind.SetProcessEcoQos
            or CommandKind.ActivateManagedPowerProfile
            or CommandKind.SetFrameRateTracking;
}

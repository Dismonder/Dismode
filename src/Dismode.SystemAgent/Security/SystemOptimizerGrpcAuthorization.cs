using Dismode.Contracts.Commands;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.Protocol;
using Dismode.Core.Ipc;
using Dismode.Core.SystemOptimization;
using Grpc.Core;

namespace Dismode.SystemAgent.Security;

internal interface ISystemOptimizerGrpcAuthorization
{
    ValueTask<AuthorizedSystemOptimizerRequest> AuthorizeAsync(
        RpcRequestMetadata? rpcMetadata,
        int serializedMessageBytes,
        CommandKind expectedCommand,
        bool requiresMutation,
        ServerCallContext context);
}

internal sealed class SystemOptimizerGrpcAuthorization :
    ISystemOptimizerGrpcAuthorization
{
    private readonly NamedPipeCallerIdentityAccessor _identityAccessor;
    private readonly SystemOptimizerCallerPolicy _callerPolicy;
    private readonly IRequestReplayGuard _replayGuard;

    internal SystemOptimizerGrpcAuthorization(
        NamedPipeCallerIdentityAccessor identityAccessor,
        SystemOptimizerCallerPolicy callerPolicy,
        IRequestReplayGuard replayGuard)
    {
        _identityAccessor = identityAccessor;
        _callerPolicy = callerPolicy;
        _replayGuard = replayGuard;
    }

    public async ValueTask<AuthorizedSystemOptimizerRequest> AuthorizeAsync(
        RpcRequestMetadata? rpcMetadata,
        int serializedMessageBytes,
        CommandKind expectedCommand,
        bool requiresMutation,
        ServerCallContext context)
    {
        if (rpcMetadata is null)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "Request metadata is required."));
        }

        RequestMetadata metadata;
        try
        {
            metadata = RpcRequestMetadataMapper.FromRpc(rpcMetadata);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, exception.Message));
        }

        if (metadata.Command != expectedCommand)
        {
            throw new RpcException(
                new Status(
                    StatusCode.InvalidArgument,
                    "The command kind does not match the invoked RPC."));
        }

        SystemOptimizerCallerIdentity caller =
            await _identityAccessor.CaptureAsync(
                    context,
                    context.CancellationToken)
                .ConfigureAwait(false);
        SystemOptimizerCallerAuthorization callerAuthorization =
            _callerPolicy.Authorize(
                caller,
                metadata.CallerSid,
                requiresMutation);
        if (!callerAuthorization.IsAuthorized)
        {
            throw new RpcException(
                new Status(
                    StatusCode.PermissionDenied,
                    callerAuthorization.Reason));
        }

        RequestValidationPolicy envelopePolicy = new(
            caller.ActualUserSid,
            _replayGuard);
        RequestValidationResult envelope = envelopePolicy.Validate(
            metadata,
            serializedMessageBytes);
        if (!envelope.IsValid)
        {
            throw new RpcException(
                new Status(
                    MapStatusCode(envelope.Failure),
                    envelope.Details ?? "The request envelope was rejected."));
        }

        return new(metadata, caller, callerAuthorization);
    }

    private static StatusCode MapStatusCode(
        RequestValidationFailure failure) =>
        failure switch
        {
            RequestValidationFailure.MessageTooLarge
                or RequestValidationFailure.ReplayCapacityExhausted =>
                    StatusCode.ResourceExhausted,
            RequestValidationFailure.CallerSidMismatch
                or RequestValidationFailure.SessionMismatch =>
                    StatusCode.PermissionDenied,
            RequestValidationFailure.ReplayDetected =>
                StatusCode.AlreadyExists,
            RequestValidationFailure.UnsupportedProtocolVersion
                or RequestValidationFailure.TimestampOutsideAllowedWindow =>
                    StatusCode.FailedPrecondition,
            _ => StatusCode.InvalidArgument,
        };
}

internal sealed record AuthorizedSystemOptimizerRequest(
    RequestMetadata Metadata,
    SystemOptimizerCallerIdentity Caller,
    SystemOptimizerCallerAuthorization Authorization);

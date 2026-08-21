using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Core.Ipc;
using Grpc.Core;

namespace GameShift.Windows.Ipc;

public sealed class GrpcRequestValidator
{
    private readonly RequestValidationPolicy _validationPolicy;

    public GrpcRequestValidator(RequestValidationPolicy validationPolicy)
    {
        _validationPolicy = validationPolicy;
    }

    public RequestMetadata Validate(
        RpcRequestMetadata? rpcMetadata,
        int serializedMessageBytes,
        CommandKind expectedCommand,
        Guid? expectedSessionId = null)
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

        RequestValidationResult validation = _validationPolicy.Validate(
            metadata,
            serializedMessageBytes,
            expectedSessionId);
        if (!validation.IsValid)
        {
            throw new RpcException(
                new Status(
                    MapStatusCode(validation.Failure),
                    validation.Details ?? "The request was rejected."));
        }

        return metadata;
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

using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;

namespace GameShift.Contracts.Protocol;

public static class RpcRequestMetadataMapper
{
    public static RequestMetadata FromRpc(RpcRequestMetadata rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);

        if (!Guid.TryParse(rpc.RequestId, out Guid requestId))
        {
            throw new FormatException("The request ID is not a valid GUID.");
        }

        Guid? sessionId = null;
        if (!string.IsNullOrWhiteSpace(rpc.SessionId))
        {
            if (!Guid.TryParse(rpc.SessionId, out Guid parsedSessionId))
            {
                throw new FormatException("The session ID is not a valid GUID.");
            }

            sessionId = parsedSessionId;
        }

        if (!Guid.TryParse(rpc.IdempotencyKey, out Guid idempotencyKey))
        {
            throw new FormatException("The idempotency key is not a valid GUID.");
        }

        int commandValue = (int)rpc.Command;
        if (!Enum.IsDefined((CommandKind)commandValue))
        {
            throw new FormatException("The command kind is not recognized.");
        }

        DateTimeOffset timestamp;
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                rpc.TimestampUnixMilliseconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new FormatException("The request timestamp is invalid.", exception);
        }

        return new(
            rpc.ProtocolVersion,
            requestId,
            sessionId,
            rpc.CallerSid,
            timestamp,
            (CommandKind)commandValue,
            rpc.Nonce,
            new IdempotencyKey(idempotencyKey));
    }

    public static RpcRequestMetadata ToRpc(RequestMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return new()
        {
            ProtocolVersion = metadata.ProtocolVersion,
            RequestId = metadata.RequestId.ToString("D"),
            SessionId = metadata.SessionId?.ToString("D") ?? string.Empty,
            CallerSid = metadata.CallerSid,
            TimestampUnixMilliseconds = metadata.TimestampUtc.ToUnixTimeMilliseconds(),
            Command = (RpcCommandKind)(int)metadata.Command,
            Nonce = metadata.Nonce,
            IdempotencyKey = metadata.IdempotencyKey.Value.ToString("D"),
        };
    }
}

using GameShift.Contracts.Commands;

namespace GameShift.Contracts.Protocol;

public sealed record RequestMetadata
{
    public RequestMetadata(
        int protocolVersion,
        Guid requestId,
        Guid? sessionId,
        string callerSid,
        DateTimeOffset timestampUtc,
        CommandKind command,
        string nonce,
        IdempotencyKey idempotencyKey)
    {
        if (protocolVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(protocolVersion),
                protocolVersion,
                "A protocol version must be positive.");
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A request ID cannot be empty.", nameof(requestId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session ID cannot be empty when supplied.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        if (nonce.Length is < 16 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nonce),
                nonce.Length,
                "A nonce must contain between 16 and 128 characters.");
        }

        ProtocolVersion = protocolVersion;
        RequestId = requestId;
        SessionId = sessionId;
        CallerSid = callerSid.Trim();
        TimestampUtc = timestampUtc.ToUniversalTime();
        Command = command;
        Nonce = nonce;
        IdempotencyKey = idempotencyKey;
    }

    public int ProtocolVersion { get; }

    public Guid RequestId { get; }

    public Guid? SessionId { get; }

    public string CallerSid { get; }

    public DateTimeOffset TimestampUtc { get; }

    public CommandKind Command { get; }

    public string Nonce { get; }

    public IdempotencyKey IdempotencyKey { get; }
}


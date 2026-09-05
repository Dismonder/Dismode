using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameShift.MemoryOptimizer.Core.Ipc;

public enum MemoryOptimizerCommand
{
    GetStatus = 1,
    GetSettings = 2,
    SaveSettings = 3,
    Optimize = 4,
    GetHistory = 5,
    PauseAutomation = 6,
    ResumeAutomation = 7,
    ShutdownTray = 8,
}

public sealed record MemoryOptimizerRequest(
    int ProtocolVersion,
    string RequestId,
    string IdempotencyKey,
    DateTimeOffset IssuedAtUtc,
    string UserSid,
    MemoryOptimizerCommand Command,
    string PayloadJson,
    string PayloadSha256);

public sealed record MemoryOptimizerResponse(
    int ProtocolVersion,
    string RequestId,
    bool Succeeded,
    string Code,
    string Message,
    string PayloadJson);

public enum ProtocolValidationCode
{
    Valid = 0,
    UnsupportedVersion = 1,
    InvalidSid = 2,
    Expired = 3,
    InvalidIntegrity = 4,
    Replay = 5,
    InvalidRequest = 6,
}

public sealed record ProtocolValidationResult(
    ProtocolValidationCode Code,
    string Message,
    MemoryOptimizerResponse? CachedResponse = null)
{
    public bool IsValid => Code == ProtocolValidationCode.Valid;
}

public static class MemoryOptimizerProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 1024 * 1024;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string CreatePipeName(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Length > 184 ||
            userSid.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Invalid Windows SID.", nameof(userSid));
        }

        return $"GameShift.MemoryOptimizer.v1.{userSid}";
    }

    public static string ComputePayloadHash(string payloadJson)
    {
        byte[] payload = Encoding.UTF8.GetBytes(payloadJson ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    public static MemoryOptimizerRequest CreateRequest<TPayload>(
        string userSid,
        MemoryOptimizerCommand command,
        TPayload payload,
        string? idempotencyKey = null,
        DateTimeOffset? issuedAtUtc = null)
    {
        string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        return new(
            Version,
            Guid.NewGuid().ToString("N"),
            idempotencyKey ?? Guid.NewGuid().ToString("N"),
            issuedAtUtc ?? DateTimeOffset.UtcNow,
            userSid,
            command,
            payloadJson,
            ComputePayloadHash(payloadJson));
    }

    public static TPayload? DeserializePayload<TPayload>(string payloadJson) =>
        JsonSerializer.Deserialize<TPayload>(payloadJson, JsonOptions);

    public static void ValidateResponse(
        MemoryOptimizerRequest request,
        MemoryOptimizerResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion != Version)
        {
            throw new InvalidDataException(
                "Service returned an unsupported protocol version.");
        }

        if (!StringComparer.Ordinal.Equals(
                response.RequestId,
                request.RequestId))
        {
            throw new InvalidDataException(
                "Service response does not match the request identifier.");
        }
    }
}

public static class ProtocolCodec
{
    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            message,
            MemoryOptimizerProtocol.JsonOptions);
        if (payload.Length == 0 ||
            payload.Length > MemoryOptimizerProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message size is invalid.");
        }

        byte[] length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(
            stream,
            lengthBytes,
            cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0 || length > MemoryOptimizerProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the 1 MiB limit.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(
            stream,
            payload,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(
                payload,
                MemoryOptimizerProtocol.JsonOptions)
            ?? throw new InvalidDataException("IPC message is empty.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(
                destination[offset..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("IPC peer closed the connection.");
            }

            offset += read;
        }
    }
}

public sealed class ReplayProtector
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _requestIds =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IdempotencyReservation>
        _idempotencyReservations =
        new(StringComparer.Ordinal);

    public ProtocolValidationResult Validate(
        MemoryOptimizerRequest request,
        string actualUserSid,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUserSid);

        RemoveExpired(nowUtc);

        if (request.ProtocolVersion != MemoryOptimizerProtocol.Version)
        {
            return new(
                ProtocolValidationCode.UnsupportedVersion,
                "Unsupported Memory Optimizer protocol version.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                request.UserSid,
                actualUserSid))
        {
            return new(
                ProtocolValidationCode.InvalidSid,
                "The authenticated pipe user does not match the request SID.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.RequestId.Length > 128 ||
            request.IdempotencyKey.Length > 128)
        {
            return new(
                ProtocolValidationCode.InvalidRequest,
                "Request identifiers are missing or too long.");
        }

        if ((nowUtc - request.IssuedAtUtc).Duration() >
            MemoryOptimizerProtocol.MaximumClockSkew)
        {
            return new(
                ProtocolValidationCode.Expired,
                "Request timestamp is outside the accepted window.");
        }

        string expectedHash = MemoryOptimizerProtocol.ComputePayloadHash(
            request.PayloadJson);
        byte[] expectedBytes;
        byte[] providedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedHash);
            providedBytes = Convert.FromHexString(request.PayloadSha256);
        }
        catch (FormatException)
        {
            return new(
                ProtocolValidationCode.InvalidIntegrity,
                "Payload checksum is malformed.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                expectedBytes,
                providedBytes))
        {
            return new(
                ProtocolValidationCode.InvalidIntegrity,
                "Payload checksum does not match.");
        }

        string requestKey = CreateScopedKey(actualUserSid, request.RequestId);
        if (!_requestIds.TryAdd(requestKey, nowUtc))
        {
            return new(
                ProtocolValidationCode.Replay,
                "Request identifier has already been used.");
        }

        string idempotencyKey = CreateScopedKey(
            actualUserSid,
            request.IdempotencyKey);
        string fingerprint = CreateRequestFingerprint(request);
        IdempotencyReservation candidate = new(
            fingerprint,
            nowUtc,
            Response: null);
        IdempotencyReservation reservation =
            _idempotencyReservations.GetOrAdd(idempotencyKey, candidate);
        if (!ReferenceEquals(reservation, candidate))
        {
            if (!StringComparer.Ordinal.Equals(
                    reservation.RequestFingerprint,
                    fingerprint))
            {
                return new(
                    ProtocolValidationCode.InvalidRequest,
                    "Idempotency key was reused for a different request.");
            }

            return reservation.Response is null
                ? new(
                    ProtocolValidationCode.Replay,
                    "An idempotent request with this key is still running.")
                : new(
                    ProtocolValidationCode.Valid,
                    "Idempotent retry.",
                    reservation.Response);
        }

        return new(ProtocolValidationCode.Valid, "Request accepted.");
    }

    public void StoreResponse(
        string actualUserSid,
        MemoryOptimizerRequest request,
        MemoryOptimizerResponse response,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualUserSid);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        string key = CreateScopedKey(actualUserSid, request.IdempotencyKey);
        string fingerprint = CreateRequestFingerprint(request);
        _idempotencyReservations.AddOrUpdate(
            key,
            _ => new(fingerprint, nowUtc, response),
            (_, existing) => StringComparer.Ordinal.Equals(
                    existing.RequestFingerprint,
                    fingerprint)
                ? existing with
                {
                    StoredAtUtc = nowUtc,
                    Response = response,
                }
                : existing);
    }

    private void RemoveExpired(DateTimeOffset nowUtc)
    {
        DateTimeOffset cutoff = nowUtc - TimeSpan.FromMinutes(10);
        foreach ((string key, DateTimeOffset timestamp) in _requestIds)
        {
            if (timestamp < cutoff)
            {
                _requestIds.TryRemove(key, out _);
            }
        }

        foreach ((string key, IdempotencyReservation value) in
                 _idempotencyReservations)
        {
            if (value.StoredAtUtc < cutoff)
            {
                _idempotencyReservations.TryRemove(key, out _);
            }
        }
    }

    private static string CreateRequestFingerprint(
        MemoryOptimizerRequest request) =>
        string.Concat(
            ((int)request.Command).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ":",
            request.PayloadSha256.ToUpperInvariant());

    private static string CreateScopedKey(string sid, string value) =>
        string.Concat(sid, ":", value);

    private sealed record IdempotencyReservation(
        string RequestFingerprint,
        DateTimeOffset StoredAtUtc,
        MemoryOptimizerResponse? Response);
}

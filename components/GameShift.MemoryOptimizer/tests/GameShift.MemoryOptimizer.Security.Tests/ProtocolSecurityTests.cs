using System.Buffers.Binary;
using GameShift.MemoryOptimizer.Core.Ipc;

namespace GameShift.MemoryOptimizer.Security.Tests;

[TestClass]
public sealed class ProtocolSecurityTests
{
    private const string UserSid = "S-1-5-21-100-200-300-1001";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ForeignSidIsRejected()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest request = CreateRequest();

        ProtocolValidationResult result = protector.Validate(
            request,
            "S-1-5-21-100-200-300-2002",
            Now);

        Assert.AreEqual(ProtocolValidationCode.InvalidSid, result.Code);
    }

    [TestMethod]
    public void ExpiredTimestampIsRejected()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest request = CreateRequest(
            issuedAtUtc: Now.AddMinutes(-3));

        ProtocolValidationResult result = protector.Validate(
            request,
            UserSid,
            Now);

        Assert.AreEqual(ProtocolValidationCode.Expired, result.Code);
    }

    [TestMethod]
    public void ChangedPayloadIsRejected()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest original = CreateRequest();
        MemoryOptimizerRequest changed = original with
        {
            PayloadJson = "{\"areas\":255}",
        };

        ProtocolValidationResult result = protector.Validate(
            changed,
            UserSid,
            Now);

        Assert.AreEqual(
            ProtocolValidationCode.InvalidIntegrity,
            result.Code);
    }

    [TestMethod]
    public void ReplayedRequestIdentifierIsRejected()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest request = CreateRequest();
        Assert.IsTrue(protector.Validate(request, UserSid, Now).IsValid);

        MemoryOptimizerRequest replay = request with
        {
            IdempotencyKey = "different-idempotency-key",
        };
        ProtocolValidationResult result = protector.Validate(
            replay,
            UserSid,
            Now.AddSeconds(1));

        Assert.AreEqual(ProtocolValidationCode.Replay, result.Code);
    }

    [TestMethod]
    public void IdempotencyKeyReturnsCachedResponse()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest request = CreateRequest();
        Assert.IsTrue(protector.Validate(request, UserSid, Now).IsValid);
        MemoryOptimizerResponse response = new(
            1,
            request.RequestId,
            true,
            "ok",
            "done",
            "{}");
        protector.StoreResponse(
            UserSid,
            request,
            response,
            Now);

        MemoryOptimizerRequest retry = request with
        {
            RequestId = "new-request-id",
            IssuedAtUtc = Now.AddSeconds(1),
        };
        ProtocolValidationResult result = protector.Validate(
            retry,
            UserSid,
            Now.AddSeconds(1));

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(response, result.CachedResponse);
    }

    [TestMethod]
    public void IdempotencyKeyCannotBeReusedForDifferentPayload()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest original = CreateRequest();
        Assert.IsTrue(protector.Validate(original, UserSid, Now).IsValid);

        string changedPayload = "{\"areas\":128}";
        MemoryOptimizerRequest changed = original with
        {
            RequestId = "changed-request-id",
            IssuedAtUtc = Now.AddSeconds(1),
            PayloadJson = changedPayload,
            PayloadSha256 = MemoryOptimizerProtocol.ComputePayloadHash(
                changedPayload),
        };
        ProtocolValidationResult result = protector.Validate(
            changed,
            UserSid,
            Now.AddSeconds(1));

        Assert.AreEqual(ProtocolValidationCode.InvalidRequest, result.Code);
    }

    [TestMethod]
    public async Task CodecRejectsMessageAboveOneMiB()
    {
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            MemoryOptimizerProtocol.MaximumMessageBytes + 1);
        await using MemoryStream stream = new(header);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => _ = await ProtocolCodec.ReadAsync<object>(
                stream,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task CodecRoundTripsProtocolMessage()
    {
        MemoryOptimizerRequest request = CreateRequest();
        await using MemoryStream stream = new();
        await ProtocolCodec.WriteAsync(
            stream,
            request,
            CancellationToken.None);
        stream.Position = 0;

        MemoryOptimizerRequest decoded = await ProtocolCodec.ReadAsync<
            MemoryOptimizerRequest>(stream, CancellationToken.None);

        Assert.AreEqual(request, decoded);
    }

    [TestMethod]
    public void ResponseMustMatchProtocolVersionAndRequestIdentifier()
    {
        MemoryOptimizerRequest request = CreateRequest();
        MemoryOptimizerResponse valid = new(
            MemoryOptimizerProtocol.Version,
            request.RequestId,
            true,
            "ok",
            "done",
            "{}");

        MemoryOptimizerProtocol.ValidateResponse(request, valid);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            MemoryOptimizerProtocol.ValidateResponse(
                request,
                valid with { ProtocolVersion = 99 }));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            MemoryOptimizerProtocol.ValidateResponse(
                request,
                valid with { RequestId = "different-request" }));
    }

    [TestMethod]
    public void DifferentWindowsSessionsUseDifferentPipeNames()
    {
        string first = MemoryOptimizerProtocol.CreatePipeName(
            "S-1-5-21-1-2-3-1001");
        string second = MemoryOptimizerProtocol.CreatePipeName(
            "S-1-5-21-1-2-3-1002");

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void PipeNameRejectsPathSeparators()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            MemoryOptimizerProtocol.CreatePipeName("S-1-5-21\\foreign"));
    }

    [TestMethod]
    public void UnsupportedProtocolVersionIsRejected()
    {
        ReplayProtector protector = new();
        MemoryOptimizerRequest request = CreateRequest() with
        {
            ProtocolVersion = 99,
        };

        ProtocolValidationResult result = protector.Validate(
            request,
            UserSid,
            Now);

        Assert.AreEqual(
            ProtocolValidationCode.UnsupportedVersion,
            result.Code);
    }

    private static MemoryOptimizerRequest CreateRequest(
        DateTimeOffset? issuedAtUtc = null) =>
        MemoryOptimizerProtocol.CreateRequest(
            UserSid,
            MemoryOptimizerCommand.Optimize,
            new { areas = 160 },
            "idempotency-key",
            issuedAtUtc ?? Now);
}

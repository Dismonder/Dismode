using GameShift.Contracts.Commands;
using GameShift.Contracts.Protocol;

namespace GameShift.UnitTests.Protocol;

[TestClass]
public sealed class RequestMetadataTests
{
    [TestMethod]
    public void ConstructorNormalizesTimestampAndKeepsClosedCommandKind()
    {
        DateTimeOffset localTimestamp = new(2026, 7, 28, 20, 0, 0, TimeSpan.FromHours(2));
        RequestMetadata metadata = new(
            protocolVersion: ProtocolInfo.CurrentVersion,
            requestId: Guid.NewGuid(),
            sessionId: Guid.NewGuid(),
            callerSid: "S-1-5-21-1000",
            timestampUtc: localTimestamp,
            command: CommandKind.StartOptimizationSession,
            nonce: "0123456789abcdef",
            idempotencyKey: IdempotencyKey.Create());

        Assert.AreEqual(TimeSpan.Zero, metadata.TimestampUtc.Offset);
        Assert.AreEqual(CommandKind.StartOptimizationSession, metadata.Command);
    }

    [TestMethod]
    public void ConstructorRejectsWeakNonce()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RequestMetadata(
                ProtocolInfo.CurrentVersion,
                Guid.NewGuid(),
                null,
                "S-1-5-21-1000",
                DateTimeOffset.UtcNow,
                CommandKind.GetComponentStatus,
                "too-short",
                IdempotencyKey.Create()));
    }

    [TestMethod]
    public void IdempotencyKeyRejectsEmptyGuid()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new IdempotencyKey(Guid.Empty));
    }
}


using GameShift.Contracts.Commands;
using GameShift.Contracts.Protocol;
using GameShift.Core.Ipc;

namespace GameShift.UnitTests.Ipc;

[TestClass]
public sealed class RequestValidationPolicyTests
{
    private const string ExpectedSid = "S-1-5-21-1000";
    private static readonly DateTimeOffset NowUtc =
        new(2026, 7, 28, 20, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ValidRequestIsAcceptedOnlyOnce()
    {
        StubTimeProvider clock = new(NowUtc);
        RequestValidationPolicy policy = new(
            ExpectedSid,
            new BoundedRequestReplayGuard(),
            clock);
        RequestMetadata request = CreateRequest(
            CommandKind.GetComponentStatus,
            sessionId: null);

        RequestValidationResult first = policy.Validate(request, 128);
        RequestValidationResult replay = policy.Validate(request, 128);

        Assert.IsTrue(first.IsValid);
        Assert.IsFalse(replay.IsValid);
        Assert.AreEqual(RequestValidationFailure.ReplayDetected, replay.Failure);
    }

    [TestMethod]
    public void InvalidEnvelopeIsRejectedBeforeReplayRegistration()
    {
        RequestValidationPolicy policy = new(
            ExpectedSid,
            new BoundedRequestReplayGuard(),
            new StubTimeProvider(NowUtc));
        RequestMetadata request = CreateRequest(
            CommandKind.GetComponentStatus,
            sessionId: null);

        RequestValidationResult wrongVersion = policy.Validate(
            request with { },
            ProtocolInfo.MaximumMessageBytes + 1);
        RequestValidationResult retry = policy.Validate(request, 128);

        Assert.AreEqual(RequestValidationFailure.MessageTooLarge, wrongVersion.Failure);
        Assert.IsTrue(retry.IsValid);
    }

    [TestMethod]
    public void CallerTimestampProtocolAndSessionAreValidated()
    {
        Guid ownedSession = Guid.NewGuid();

        AssertFailure(
            CreateRequest(
                CommandKind.GetComponentStatus,
                sessionId: null,
                callerSid: "S-1-5-21-OTHER"),
            RequestValidationFailure.CallerSidMismatch,
            expectedSessionId: null);
        AssertFailure(
            CreateRequest(
                CommandKind.GetComponentStatus,
                sessionId: null,
                protocolVersion: ProtocolInfo.CurrentVersion + 1),
            RequestValidationFailure.UnsupportedProtocolVersion,
            expectedSessionId: null);
        AssertFailure(
            CreateRequest(
                CommandKind.GetComponentStatus,
                sessionId: null,
                timestampUtc: NowUtc - ProtocolInfo.MaximumClockSkew - TimeSpan.FromSeconds(1)),
            RequestValidationFailure.TimestampOutsideAllowedWindow,
            expectedSessionId: null);
        AssertFailure(
            CreateRequest(
                CommandKind.RestoreOptimizationSession,
                sessionId: Guid.NewGuid()),
            RequestValidationFailure.SessionMismatch,
            expectedSessionId: ownedSession);
        AssertFailure(
            CreateRequest(
                CommandKind.CloseGame,
                sessionId: Guid.NewGuid()),
            RequestValidationFailure.SessionMismatch,
            expectedSessionId: ownedSession);
    }

    [TestMethod]
    public void ReplayGuardDoesNotEvictLiveEntriesWhenCapacityIsReached()
    {
        BoundedRequestReplayGuard guard = new(capacity: 1);

        ReplayRegistrationResult first = guard.TryRegister(Guid.NewGuid(), NowUtc);
        ReplayRegistrationResult second = guard.TryRegister(Guid.NewGuid(), NowUtc);

        Assert.AreEqual(ReplayRegistrationResult.Registered, first);
        Assert.AreEqual(ReplayRegistrationResult.CapacityExhausted, second);
    }

    private static void AssertFailure(
        RequestMetadata request,
        RequestValidationFailure expectedFailure,
        Guid? expectedSessionId)
    {
        RequestValidationPolicy policy = new(
            ExpectedSid,
            new BoundedRequestReplayGuard(),
            new StubTimeProvider(NowUtc));

        RequestValidationResult result = policy.Validate(
            request,
            serializedMessageBytes: 128,
            expectedSessionId);

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual(expectedFailure, result.Failure);
    }

    private static RequestMetadata CreateRequest(
        CommandKind command,
        Guid? sessionId,
        string callerSid = ExpectedSid,
        int protocolVersion = ProtocolInfo.CurrentVersion,
        DateTimeOffset? timestampUtc = null) =>
        new(
            protocolVersion,
            Guid.NewGuid(),
            sessionId,
            callerSid,
            timestampUtc ?? NowUtc,
            command,
            "0123456789abcdef",
            IdempotencyKey.Create());

    private sealed class StubTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public StubTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}

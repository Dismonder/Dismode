using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using Dismode.Core.Activation;

namespace Dismode.UnitTests;

[TestClass]
public sealed class UiActivationServerTests
{
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(10);
    private static readonly string OwnExecutable =
        Path.GetFullPath("Dismode.UI.exe");

    [TestMethod]
    public async Task SilentClientDoesNotBlockTheNextRequest()
    {
        string sid = FakeSid();
        List<UiActivationRequest> accepted = [];
        using UiActivationServer server = new(
            sid,
            request =>
            {
                lock (accepted)
                {
                    accepted.Add(request);
                }
            },
            TimeSpan.FromMilliseconds(300));

        using NamedPipeClientStream silent = OpenClient(sid);
        await silent.ConnectAsync((int)ClientTimeout.TotalMilliseconds);

        UiActivationRequest request = ShowRequest();
        bool sent = UiActivationClient.TrySend(
            sid,
            request,
            ClientTimeout,
            out string? failure);

        Assert.IsTrue(sent, "TrySend: " + failure);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () =>
                {
                    lock (accepted)
                    {
                        return accepted.Count == 1;
                    }
                },
                ClientTimeout),
            "Zadanie ma dotrzec do okna.");
        Assert.AreEqual(request.RequestId, accepted[0].RequestId);
        Assert.IsTrue(accepted[0].ShowOnly);
    }

    [TestMethod]
    public async Task GarbageAndOversizedFramesAreRejectedAndServiceContinues()
    {
        string sid = FakeSid();
        int acceptedCount = 0;
        using UiActivationServer server = new(
            sid,
            _ => Interlocked.Increment(ref acceptedCount));

        byte[] garbage = new byte[sizeof(int) + 5];
        BinaryPrimitives.WriteInt32LittleEndian(garbage, 5);
        "xxxxx"u8.CopyTo(garbage.AsSpan(sizeof(int)));
        Assert.AreEqual(0, await SendRawAsync(sid, garbage));

        byte[] oversized = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            oversized,
            UiActivationProtocol.MaximumMessageBytes + 1);
        Assert.AreEqual(0, await SendRawAsync(sid, oversized));

        Assert.IsTrue(UiActivationClient.TrySend(
            sid,
            ShowRequest(),
            ClientTimeout,
            out string? failure), failure);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref acceptedCount) == 1,
                ClientTimeout));
    }

    [TestMethod]
    public void RepeatedRequestIdIsRejected()
    {
        string sid = FakeSid();
        int acceptedCount = 0;
        using UiActivationServer server = new(
            sid,
            _ => Interlocked.Increment(ref acceptedCount));
        UiActivationRequest request = ShowRequest();

        Assert.IsTrue(UiActivationClient.TrySend(
            sid,
            request,
            ClientTimeout,
            out string? firstFailure), firstFailure);
        Assert.IsFalse(UiActivationClient.TrySend(
            sid,
            request,
            ClientTimeout,
            out string? secondFailure));

        Assert.IsNotNull(secondFailure);
        Assert.Contains("odrzuciło", secondFailure);
        Assert.IsTrue(
            SpinWait.SpinUntil(
                () => Volatile.Read(ref acceptedCount) == 1,
                ClientTimeout));
    }

    [TestMethod]
    public void DisposeStopsListening()
    {
        string sid = FakeSid();
        UiActivationServer server = new(sid, _ => { });
        Assert.IsTrue(UiActivationClient.TrySend(
            sid,
            ShowRequest(),
            ClientTimeout,
            out string? failure), failure);

        server.Dispose();

        Assert.IsFalse(UiActivationClient.TrySend(
            sid,
            ShowRequest(),
            TimeSpan.FromMilliseconds(500),
            out _));
    }

    private static string FakeSid() =>
        "S-1-5-21-" + Guid.NewGuid().ToString("N");

    private static UiActivationRequest ShowRequest() =>
        StartupPolicy.BuildHandOverRequest(
            new(StartInBackground: false, GameExecutablePath: null),
            OwnExecutable,
            DateTimeOffset.UtcNow);

    private static NamedPipeClientStream OpenClient(string sid) => new(
        ".",
        UiActivationProtocol.GetPipeName(sid),
        PipeDirection.InOut,
        PipeOptions.None,
        TokenImpersonationLevel.Identification);

    private static async Task<int> SendRawAsync(string sid, byte[] frame)
    {
        using NamedPipeClientStream pipe = OpenClient(sid);
        await pipe.ConnectAsync((int)ClientTimeout.TotalMilliseconds);
        await pipe.WriteAsync(frame);
        await pipe.FlushAsync();
        return pipe.ReadByte();
    }
}

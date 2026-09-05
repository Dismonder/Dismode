using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerGameProfileClientTests
{
    [TestMethod]
    public async Task UserCancellationIsPropagated()
    {
        using SystemOptimizerGameProfileClient client = new("S-1-5-21-1000");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ProcessIdentity identity = new(
            new(Environment.ProcessId, DateTimeOffset.UtcNow),
            Path.GetFullPath("game.exe"),
            new string('A', 64),
            publisher: null,
            "S-1-5-21-1000",
            sessionId: 1,
            parentRuntimeKey: null);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            client.ActivateAsync(
                    GameProfileId.Create(),
                    identity,
                    cancellation.Token)
                .AsTask());
    }
}

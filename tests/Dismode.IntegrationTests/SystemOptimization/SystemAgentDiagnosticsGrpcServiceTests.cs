using Dismode.Contracts.Commands;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.Protocol;
using Dismode.Core.SystemOptimization;
using Dismode.SystemAgent.Ipc;
using Dismode.SystemAgent.Security;
using Dismode.Windows.Services;
using Grpc.Core;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemAgentDiagnosticsGrpcServiceTests
{
    [TestMethod]
    public async Task GetStatusReportsServiceReadyAndReadOnlyBoundary()
    {
        SystemAgentDiagnosticsGrpcService service = new(
            new AllowReadOnlyAuthorization(),
            new FixedServiceInventory([]));

        StatusReply reply = await service.GetStatus(
            new StatusRequest(),
            null!);

        Assert.AreEqual("SystemAgent", reply.Component);
        Assert.AreEqual("Ready", reply.State);
        Assert.AreEqual(ProtocolInfo.CurrentVersion, reply.ProtocolVersion);
        StringAssert.Contains(reply.Message, "podpis");
    }

    [TestMethod]
    public async Task DiscoverLimitsReturnedInventoryWithoutLosingTotalCount()
    {
        ServiceSnapshot first = CreateService("First");
        ServiceSnapshot second = CreateService("Second");
        SystemAgentDiagnosticsGrpcService service = new(
            new AllowReadOnlyAuthorization(),
            new FixedServiceInventory([first, second]));

        DiscoveryReply reply = await service.Discover(
            new DiscoveryRequest { MaximumItems = 1 },
            null!);

        Assert.AreEqual(2, reply.TotalServiceCount);
        Assert.IsTrue(reply.Truncated);
        Assert.HasCount(1, reply.Services);
        Assert.AreEqual("First", reply.Services[0].ServiceName);
    }

    private static ServiceSnapshot CreateService(string name) =>
        new(
            name,
            $"{name} service",
            "Running",
            CanStop: true,
            "Win32OwnProcess",
            Dependencies: [],
            "Automatic",
            $"C:\\Program Files\\Dismode\\{name}.exe",
            "LocalSystem",
            IsDriver: false,
            IsSharedProcess: false,
            TriggerCount: 0,
            RunningDependentServices: []);

    private sealed class FixedServiceInventory(
        IReadOnlyList<ServiceSnapshot> snapshots) : IServiceInventory
    {
        public IReadOnlyList<ServiceSnapshot> Capture() => snapshots;
    }

    private sealed class AllowReadOnlyAuthorization :
        ISystemOptimizerGrpcAuthorization
    {
        public ValueTask<AuthorizedSystemOptimizerRequest> AuthorizeAsync(
            RpcRequestMetadata? rpcMetadata,
            int serializedMessageBytes,
            CommandKind expectedCommand,
            bool requiresMutation,
            ServerCallContext context)
        {
            Assert.IsFalse(requiresMutation);
            RequestMetadata metadata = new(
                ProtocolInfo.CurrentVersion,
                Guid.NewGuid(),
                sessionId: null,
                "S-1-5-21-1000",
                DateTimeOffset.UtcNow,
                expectedCommand,
                "0123456789abcdef",
                IdempotencyKey.Create());
            return ValueTask.FromResult(
                new AuthorizedSystemOptimizerRequest(
                    metadata,
                    new(
                        "S-1-5-21-1000",
                        123,
                        "C:\\Program Files\\Dismode\\Dismode.SystemOptimizer.exe",
                        HasValidAuthenticodeSignature: false,
                        IsTrustedSigner: false),
                    new(
                        IsAuthorized: true,
                        IsReadOnly: true,
                        "Read-only test client.")));
        }
    }
}

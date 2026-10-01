using Dismode.Core.SystemOptimization;
using Dismode.SystemAgent;
using Dismode.SystemAgent.Ipc;
using Dismode.SystemAgent.Security;
using Dismode.SystemAgent.SystemOptimization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class SystemAgentServiceRegistrationTests
{
    [TestMethod]
    public void AddSystemOptimizerServicesBuildsCompleteServiceGraph()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Dismode.Tests",
            Guid.NewGuid().ToString("N"));
        ServiceCollection services = new();

        SystemAgentServiceRegistration.AddSystemOptimizerServices(
            services,
            Path.Combine(root, "system-optimizer.db"),
            Path.Combine(root, "machine-recovery.jsonl"),
            Path.Combine(root, "install"),
            trustedSignerThumbprints: []);

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        Assert.IsNotNull(
            provider.GetRequiredService<ISystemOptimizerGrpcAuthorization>());
        Assert.IsNotNull(
            provider.GetRequiredService<SystemOptimizerGrpcService>());
        Assert.IsNotNull(
            provider.GetRequiredService<SystemAgentDiagnosticsGrpcService>());
        Assert.IsNotNull(
            provider.GetServices<IHostedService>()
                .Single(service =>
                    service is SystemOptimizerRecoveryHostedService));
        Assert.IsNotNull(
            provider.GetRequiredService<IHardwareFingerprintProvider>());
    }
}

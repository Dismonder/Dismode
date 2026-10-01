using Dismode.Core.Ipc;
using Dismode.Core.Journal;
using Dismode.Core.SystemOptimization;
using Dismode.Data.Journal;
using Dismode.Data.SystemOptimization;
using Dismode.SystemAgent.Ipc;
using Dismode.SystemAgent.Security;
using Dismode.SystemAgent.SystemOptimization;
using Dismode.Windows.Security;
using Dismode.Windows.Services;
using Dismode.Windows.SystemOptimization;
using Microsoft.Extensions.DependencyInjection;

namespace Dismode.SystemAgent;

internal static class SystemAgentServiceRegistration
{
    internal static IServiceCollection AddSystemOptimizerServices(
        IServiceCollection services,
        string databasePath,
        string recoveryJournalPath,
        string installationRoot,
        IReadOnlyList<string> trustedSignerThumbprints)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryJournalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        ArgumentNullException.ThrowIfNull(trustedSignerThumbprints);

        string fullDatabasePath = Path.GetFullPath(databasePath);
        string fullJournalPath = Path.GetFullPath(recoveryJournalPath);
        string fullInstallationRoot = Path.GetFullPath(installationRoot);

        services.AddSingleton(_ =>
            new SqliteSystemOptimizerStore(fullDatabasePath));
        services.AddSingleton<IRecoveryJournal>(_ =>
            new AppendOnlyRecoveryJournal(fullJournalPath));
        services.AddSingleton<IHardwareFingerprintProvider,
            WindowsHardwareFingerprintProvider>();
        services.AddSingleton(
            new AuthenticodeSignatureVerifier(trustedSignerThumbprints));
        services.AddSingleton(
            new SystemOptimizerCallerPolicy(fullInstallationRoot));
        services.AddSingleton<IRequestReplayGuard,
            BoundedRequestReplayGuard>();
        services.AddSingleton(provider =>
            new NamedPipeCallerIdentityAccessor(
                provider.GetRequiredService<AuthenticodeSignatureVerifier>()));
        services.AddSingleton<ISystemOptimizerGrpcAuthorization>(provider =>
            new SystemOptimizerGrpcAuthorization(
                provider.GetRequiredService<NamedPipeCallerIdentityAccessor>(),
                provider.GetRequiredService<SystemOptimizerCallerPolicy>(),
                provider.GetRequiredService<IRequestReplayGuard>()));
        services.AddSingleton<IServiceInventory, ServiceInventory>();
        services.AddSingleton(provider =>
            new WindowsSystemTweakRuntime(
                provider.GetRequiredService<IRecoveryJournal>(),
                provider.GetRequiredService<SqliteSystemOptimizerStore>()));
        services.AddSingleton<ISystemTweakRuntime>(provider =>
            provider.GetRequiredService<WindowsSystemTweakRuntime>());
        services.AddSingleton(provider =>
            new SystemOptimizerCoordinator(
                provider.GetRequiredService<SqliteSystemOptimizerStore>(),
                provider.GetRequiredService<IHardwareFingerprintProvider>(),
                provider.GetRequiredService<ISystemTweakRuntime>(),
                fullJournalPath));
        services.AddSingleton<ISystemOptimizerRecoveryMaintenance>(provider =>
            provider.GetRequiredService<SystemOptimizerCoordinator>());
        services.AddSingleton(provider =>
            new SystemOptimizerGrpcService(
                provider.GetRequiredService<ISystemOptimizerGrpcAuthorization>(),
                provider.GetRequiredService<SystemOptimizerCoordinator>(),
                provider.GetRequiredService<SqliteSystemOptimizerStore>()));
        services.AddSingleton(provider =>
            new SystemAgentDiagnosticsGrpcService(
                provider.GetRequiredService<ISystemOptimizerGrpcAuthorization>(),
                provider.GetRequiredService<IServiceInventory>()));
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            provider => new SystemOptimizerRecoveryHostedService(
                provider.GetRequiredService<
                    ISystemOptimizerRecoveryMaintenance>()));
        return services;
    }
}

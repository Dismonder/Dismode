using GameShift.Core.Ipc;
using GameShift.Core.Journal;
using GameShift.Core.SystemOptimization;
using GameShift.Data.Journal;
using GameShift.Data.SystemOptimization;
using GameShift.SystemAgent.Ipc;
using GameShift.SystemAgent.Security;
using GameShift.SystemAgent.SystemOptimization;
using GameShift.Windows.Security;
using GameShift.Windows.Services;
using GameShift.Windows.SystemOptimization;
using Microsoft.Extensions.DependencyInjection;

namespace GameShift.SystemAgent;

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

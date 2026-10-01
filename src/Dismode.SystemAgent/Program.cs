using System.Text.Json;
using Dismode.Contracts.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Product;
using Dismode.Data.Storage;
using Dismode.Data.SystemOptimization;
using Dismode.SystemAgent;
using Dismode.SystemAgent.Ipc;
using Dismode.SystemAgent.Security;
using Dismode.Windows.Platform;
using Dismode.Windows.Security;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

if (!WindowsPlatformSupport.IsSupported)
{
    Console.Error.WriteLine(
        $"{ProductInformation.DisplayName} SystemAgent requires Windows 11 23H2 or newer.");
    return 2;
}

if (args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
{
    ComponentStatus status = new(
        Component: "SystemAgent",
        State: "ReadOnlyGrpcReady-NoSystemMutations",
        ProtocolVersion: ProtocolInfo.CurrentVersion,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    Console.WriteLine(JsonSerializer.Serialize(status));
    Console.WriteLine($"Machine state directory: {DismodeStoragePaths.MachineDataDirectory}");
    Console.WriteLine($"System pipe: {PipeNames.System}");
    return 0;
}

// Instalator przenosi katalog maszyny przed startem uslugi; to jest zapas
// na wypadek, gdyby tamten krok sie nie udal.
bool legacyMachineDataPresent = Directory.Exists(
    LegacyStorageMigration.LegacyMachineDataDirectory);
foreach (string problem in LegacyStorageMigration.MigrateMachineData().Problems)
{
    Console.Error.WriteLine($"Migracja danych GameShift: {problem}");
}

if (legacyMachineDataPresent)
{
    // Pliki przejete tutaj, a nie przez instalator, przynosza wlasciciela
    // i jawne wpisy ACL sprzed zmiany nazwy, a instalator juz ich nie
    // zresetuje. Shared zachowuje swoj wpis dla Users; dzieci dziedzicza.
    foreach (string problem in MachineDataAccessControl.ResetChildrenToInherited(
                 DismodeStoragePaths.MachineDataDirectory,
                 Path.Combine(DismodeStoragePaths.MachineDataDirectory, "Shared")))
    {
        Console.Error.WriteLine($"Uprawnienia danych maszyny: {problem}");
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DismodeSystemAgent";
});

builder.WebHost.UseNamedPipes(options =>
{
    options.CurrentUserOnly = false;
    options.PipeSecurity = NamedPipeSecurityFactory.CreateSystemService();
    options.MaxReadBufferSize = ProtocolInfo.MaximumMessageBytes;
    options.MaxWriteBufferSize = ProtocolInfo.MaximumMessageBytes;
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
    serverOptions.ListenNamedPipe(
        PipeNames.System,
        listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
        });
});

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = ProtocolInfo.MaximumMessageBytes;
    options.MaxSendMessageSize = ProtocolInfo.MaximumMessageBytes;
});
SystemAgentServiceRegistration.AddSystemOptimizerServices(
    builder.Services,
    DismodeStoragePaths.SystemOptimizerDatabasePath,
    DismodeStoragePaths.MachineRecoveryJournalPath,
    AppContext.BaseDirectory,
    TrustedSignerConfiguration.Load());

WebApplication app = builder.Build();
await app.Services
    .GetRequiredService<SqliteSystemOptimizerStore>()
    .InitializeAsync(CancellationToken.None);
app.MapGrpcService<SystemAgentDiagnosticsGrpcService>();
app.MapGrpcService<SystemOptimizerGrpcService>();

Console.WriteLine($"Dismode SystemAgent listening on {PipeNames.System}.");
await app.RunAsync();
return 0;

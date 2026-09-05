using System.Text.Json;
using GameShift.Contracts.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Product;
using GameShift.Data.Storage;
using GameShift.Data.SystemOptimization;
using GameShift.SystemAgent;
using GameShift.SystemAgent.Ipc;
using GameShift.SystemAgent.Security;
using GameShift.Windows.Platform;
using GameShift.Windows.Security;
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
    Console.WriteLine($"Machine state directory: {GameShiftStoragePaths.MachineDataDirectory}");
    Console.WriteLine($"System pipe: {PipeNames.System}");
    return 0;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "GameShiftSystemAgent";
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
    GameShiftStoragePaths.SystemOptimizerDatabasePath,
    GameShiftStoragePaths.MachineRecoveryJournalPath,
    AppContext.BaseDirectory,
    TrustedSignerConfiguration.Load());

WebApplication app = builder.Build();
await app.Services
    .GetRequiredService<SqliteSystemOptimizerStore>()
    .InitializeAsync(CancellationToken.None);
app.MapGrpcService<SystemAgentDiagnosticsGrpcService>();
app.MapGrpcService<SystemOptimizerGrpcService>();

Console.WriteLine($"GameShift SystemAgent listening on {PipeNames.System}.");
await app.RunAsync();
return 0;

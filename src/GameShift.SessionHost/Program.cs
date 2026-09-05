using System.Security.Principal;
using System.Text.Json;
using GameShift.Contracts.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Ipc;
using GameShift.Core.Product;
using GameShift.Data.Journal;
using GameShift.Data.Storage;
using GameShift.Data.UserData;
using GameShift.SessionHost;
using GameShift.Windows.Ipc;
using GameShift.Windows.Platform;
using GameShift.Windows.Processes;
using GameShift.Windows.Security;
using GameShift.Windows.Sessions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

if (!WindowsPlatformSupport.IsSupported)
{
    Console.Error.WriteLine(
        $"{ProductInformation.DisplayName} SessionHost requires Windows 11 23H2 or newer.");
    return 2;
}

if (args.Length == 1)
{
    bool prepareUpdate = string.Equals(
        args[0],
        "--prepare-update",
        StringComparison.OrdinalIgnoreCase);
    bool prepareUninstall = string.Equals(
        args[0],
        "--prepare-uninstall",
        StringComparison.OrdinalIgnoreCase);
    bool shutdownComponents = string.Equals(
        args[0],
        "--shutdown-components",
        StringComparison.OrdinalIgnoreCase);
    if (prepareUpdate || prepareUninstall || shutdownComponents)
    {
        return await SessionHostUpdatePreparation.RunAsync(
            stopUserInterface: prepareUpdate || prepareUninstall,
            restoreSystemOptimizer: prepareUninstall,
            cancellationToken: CancellationToken.None);
    }
}

SecurityIdentifier userSid = CurrentWindowsIdentity.GetUserSid();

if (args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase))
{
    ComponentStatus status = new(
        Component: "SessionHost",
        State: "ReadOnlyGrpcReady",
        ProtocolVersion: ProtocolInfo.CurrentVersion,
        ObservedAtUtc: DateTimeOffset.UtcNow);

    Console.WriteLine(JsonSerializer.Serialize(status));
    Console.WriteLine(WindowsPlatformSupport.DescribeCurrentSystem());
    Console.WriteLine($"User pipe: {PipeNames.ForUser(userSid.Value)}");
    return 0;
}

string pipeName = PipeNames.ForUser(userSid.Value);
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseNamedPipes(options =>
{
    options.CurrentUserOnly = false;
    options.PipeSecurity = NamedPipeSecurityFactory.Create(userSid);
    options.MaxReadBufferSize = ProtocolInfo.MaximumMessageBytes;
    options.MaxWriteBufferSize = ProtocolInfo.MaximumMessageBytes;
});

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AddServerHeader = false;
    serverOptions.ListenNamedPipe(
        pipeName,
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

RequestValidationPolicy validationPolicy = new(
    userSid.Value,
    new BoundedRequestReplayGuard());
ReadOnlyDiagnosticsGrpcService diagnosticsService = new(
    componentName: "SessionHost",
    validationPolicy,
    processInventory: new ProcessInventory(),
    serviceInventory: null);
using SqliteUserDataStore userDataStore = new();
using AppendOnlyRecoveryJournal recoveryJournal = new(
    GameShiftStoragePaths.UserRecoveryJournalPath);
using SystemOptimizerGameProfileClient systemProfileCoordinator = new(
    userSid.Value);
await using LocalGameSessionOrchestrator sessionOrchestrator = new(
    userDataStore,
    userDataStore,
    recoveryJournal,
    optimizationPreferences: userDataStore,
    systemProfileCoordinator: systemProfileCoordinator);
await sessionOrchestrator.InitializeAsync(CancellationToken.None);
GameShiftSessionGrpcService sessionService = new(
    validationPolicy,
    sessionOrchestrator);
builder.Services.AddSingleton(diagnosticsService);
builder.Services.AddSingleton(sessionService);

WebApplication app = builder.Build();
app.MapGrpcService<ReadOnlyDiagnosticsGrpcService>();
app.MapGrpcService<GameShiftSessionGrpcService>();

Console.WriteLine($"GameShift SessionHost listening on {pipeName}.");
await app.RunAsync();
return 0;

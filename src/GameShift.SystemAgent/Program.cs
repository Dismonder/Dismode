using System.Security.Principal;
using System.Text.Json;
using GameShift.Contracts.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Ipc;
using GameShift.Core.Product;
using GameShift.Data.Storage;
using GameShift.Windows.Ipc;
using GameShift.Windows.Platform;
using GameShift.Windows.Security;
using GameShift.Windows.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

if (!WindowsPlatformSupport.IsSupported)
{
    Console.Error.WriteLine(
        $"{ProductInformation.DisplayName} SystemAgent requires Windows 11 23H2 or newer.");
    return 2;
}

SecurityIdentifier allowedUserSid = CurrentWindowsIdentity.GetUserSid();

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

builder.WebHost.UseNamedPipes(options =>
{
    options.CurrentUserOnly = false;
    options.PipeSecurity = NamedPipeSecurityFactory.Create(allowedUserSid);
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

RequestValidationPolicy validationPolicy = new(
    allowedUserSid.Value,
    new BoundedRequestReplayGuard());
ReadOnlyDiagnosticsGrpcService diagnosticsService = new(
    componentName: "SystemAgent",
    validationPolicy,
    processInventory: null,
    serviceInventory: new ServiceInventory());
builder.Services.AddSingleton(diagnosticsService);

WebApplication app = builder.Build();
app.MapGrpcService<ReadOnlyDiagnosticsGrpcService>();

Console.WriteLine($"GameShift SystemAgent listening on {PipeNames.System}.");
await app.RunAsync();
return 0;

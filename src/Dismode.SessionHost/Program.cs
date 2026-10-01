using System.Security.Principal;
using System.Text.Json;
using Dismode.Contracts.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Activation;
using Dismode.Core.Ipc;
using Dismode.Core.Product;
using Dismode.Data.Journal;
using Dismode.Data.Storage;
using Dismode.Data.UserData;
using Dismode.SessionHost;
using Dismode.Windows.Ipc;
using Dismode.Windows.Platform;
using Dismode.Windows.Processes;
using Dismode.Windows.Security;
using Dismode.Windows.Sessions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

if (!WindowsPlatformSupport.IsSupported)
{
    Console.Error.WriteLine(
        $"{ProductInformation.DisplayName} SessionHost requires Windows 11 23H2 or newer.");
    return 2;
}

// Poprzednia wersja (GameShift) w tej sesji optymalizuje te same gry i, do
// pierwszego startu Dismode, trzyma ten sam katalog danych. Jej mutexy i rury
// nosza stara nazwe, wiec blokada pojedynczego egzemplarza nizej jej nie
// widzi; migracja spod dzialajacego programu rozdzielilaby dane na pol.
if (LegacyProductProcesses.FindRunningInCurrentSession() is string legacyComponent)
{
    Console.Error.WriteLine(
        "Działa jeszcze poprzednia wersja programu (GameShift): "
        + legacyComponent
        + ". Zamknij ją i uruchom Dismode ponownie.");
    return StartupPolicy.HostExitRefused;
}

// Dane z wydan pod stara nazwa (GameShift) musza byc na miejscu, zanim
// ktorykolwiek tryb, lacznie z brama aktualizacji, przeczyta dziennik.
LegacyStorageMigrationResult migration = LegacyStorageMigration.MigrateUserData();
foreach (string problem in migration.Problems)
{
    Console.Error.WriteLine($"Migracja danych GameShift: {problem}");
}

if (migration.BlocksStartup)
{
    // Bez bazy i dziennika host zalozylby puste; uzytkownik zobaczylby program,
    // ktory zapomnial biblioteke i nie dokonczy recovery.
    Console.Error.WriteLine(
        "Migracja danych GameShift: baza profili albo dziennik recovery "
        + "poprzedniej wersji są w użyciu. Zamknij programy, które je trzymają, "
        + "i uruchom Dismode ponownie.");
    return StartupPolicy.HostExitRefused;
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

// Jeden host na sesje uzytkownika, niezaleznie od katalogu: drugi
// egzemplarz walczylby z pierwszym o te sama rure i dziennik. Pomocnicze
// tryby wyzej (--diagnostics, --prepare-update, --shutdown-components)
// dzialaja obok hosta i celowo nie przechodza przez te blokade.
using SingleInstanceLock? hostInstance = SingleInstanceLock.TryAcquire(
    SingleInstanceLock.BuildName("SessionHost", userSid.Value));
if (hostInstance is null)
{
    Console.Error.WriteLine(
        "Dismode.SessionHost już działa dla tego użytkownika; drugi "
        + "egzemplarz nie wystartuje.");
    return 3;
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
    DismodeStoragePaths.UserRecoveryJournalPath);
using SystemOptimizerGameProfileClient systemProfileCoordinator = new(
    userSid.Value);
await using LocalGameSessionOrchestrator sessionOrchestrator = new(
    userDataStore,
    userDataStore,
    recoveryJournal,
    optimizationPreferences: userDataStore,
    systemProfileCoordinator: systemProfileCoordinator,
    globalBackgroundRules: userDataStore);
await sessionOrchestrator.InitializeAsync(CancellationToken.None);
DismodeSessionGrpcService sessionService = new(
    validationPolicy,
    sessionOrchestrator);
builder.Services.AddSingleton(diagnosticsService);
builder.Services.AddSingleton(sessionService);

WebApplication app = builder.Build();
app.MapGrpcService<ReadOnlyDiagnosticsGrpcService>();
app.MapGrpcService<DismodeSessionGrpcService>();

Console.WriteLine($"Dismode SessionHost listening on {pipeName}.");
await app.RunAsync();
return 0;

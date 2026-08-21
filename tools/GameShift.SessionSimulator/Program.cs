using System.Globalization;
using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Data.Journal;
using GameShift.Data.Storage;
using GameShift.Windows.Ipc;
using GameShift.Windows.Processes;
using GameShift.Windows.Security;
using Grpc.Core;
using Grpc.Net.Client;

const int defaultMaximumItems = 25;
TimeSpan timeout = TimeSpan.FromSeconds(10);

if (args.Contains(
        "--presentmon-status",
        StringComparer.OrdinalIgnoreCase))
{
    return await CheckPresentMonStatusAsync(args);
}

if (args.Contains(
        "--journal-status",
        StringComparer.OrdinalIgnoreCase))
{
    return await CheckJournalStatusAsync();
}

if (args.Contains(
        "--restore-active-session",
        StringComparer.OrdinalIgnoreCase))
{
    return await RestoreActiveSessionAsync();
}

string target = ReadOption(args, "--pipe") ?? "both";
int maximumItems = ReadMaximumItems(args);
bool runSecuritySelfTest = args.Any(argument =>
    string.Equals(
        argument,
        "--security-self-test",
        StringComparison.OrdinalIgnoreCase));
string callerSid = CurrentWindowsIdentity.GetUserSid().Value;

IReadOnlyList<(string Label, string PipeName)> endpoints = target.ToLowerInvariant() switch
{
    "user" => [("SessionHost", PipeNames.ForUser(callerSid))],
    "system" => [("SystemAgent", PipeNames.System)],
    "both" =>
    [
        ("SessionHost", PipeNames.ForUser(callerSid)),
        ("SystemAgent", PipeNames.System),
    ],
    _ => throw new ArgumentException(
        "--pipe must be one of: user, system, both."),
};

foreach ((string label, string pipeName) in endpoints)
{
    await ProbeAsync(label, pipeName, callerSid, maximumItems, timeout);
    if (runSecuritySelfTest)
    {
        await ProbeSecurityAsync(
            label,
            pipeName,
            callerSid,
            timeout);
    }
}

return 0;

static async Task<int> CheckPresentMonStatusAsync(string[] arguments)
{
    string executablePath =
        ReadOption(arguments, "--presentmon-path")
        ?? PresentMonComponent.ResolveDefaultExecutablePath();
    PresentMonComponentInspection inspection =
        await PresentMonComponent.InspectAsync(
            executablePath,
            CancellationToken.None);
    Console.WriteLine(
        $"PresentMon {PresentMonComponent.Version}: "
        + $"{inspection.State}; {inspection.Message}; "
        + $"path: {inspection.ExecutablePath}");
    return inspection.IsReady ? 0 : 2;
}

static async Task<int> CheckJournalStatusAsync()
{
    string journalPath = GameShiftStoragePaths.UserRecoveryJournalPath;
    RecoveryJournalInspection inspection =
        await RecoveryJournalInspector.InspectAsync(
            journalPath,
            CancellationToken.None);
    if (inspection.IsClean)
    {
        Console.WriteLine(
            $"Recovery journal: clean ({inspection.RecordCount} records).");
        return 0;
    }

    Console.Error.WriteLine(
        "Recovery journal: unfinished session(s): "
        + string.Join(
            ", ",
            inspection.UnfinishedSessionIds.Select(
                sessionId => sessionId.ToString("D"))));
    return 3;
}

static async Task<int> RestoreActiveSessionAsync()
{
    string callerSid = CurrentWindowsIdentity.GetUserSid().Value;
    using GrpcChannel channel = NamedPipeGrpcChannelFactory.Create(
        PipeNames.ForUser(callerSid));
    GameShiftSessions.GameShiftSessionsClient client = new(channel);
    using CancellationTokenSource cancellation =
        new(TimeSpan.FromSeconds(60));
    SessionStateReply active = await client.GetActiveSessionAsync(
        new GetActiveSessionRequest
        {
            Metadata = CreateMetadata(
                callerSid,
                CommandKind.GetActiveSession),
        },
        cancellationToken: cancellation.Token);
    if (!active.HasSession)
    {
        Console.WriteLine("SessionHost session: none; nothing to restore.");
        return 0;
    }

    RpcRequestMetadata metadata = CreateMetadata(
        callerSid,
        CommandKind.RestoreOptimizationSession);
    metadata.SessionId = active.SessionId;
    SessionStateReply restored = await client.RestoreSessionAsync(
        new RestoreSessionRequest
        {
            Metadata = metadata,
        },
        cancellationToken: cancellation.Token);
    Console.WriteLine(
        $"Restored session {restored.SessionId}: {restored.State}; "
        + $"{restored.Message}");
    return restored.ErrorCount == 0 ? 0 : 3;
}

static async Task ProbeAsync(
    string label,
    string pipeName,
    string callerSid,
    int maximumItems,
    TimeSpan timeout)
{
    using GrpcChannel channel = NamedPipeGrpcChannelFactory.Create(pipeName);
    GameShiftDiagnostics.GameShiftDiagnosticsClient client = new(channel);

    using CancellationTokenSource cancellation = new(timeout);

    StatusReply status = await client.GetStatusAsync(
        new StatusRequest
        {
            Metadata = CreateMetadata(
                callerSid,
                CommandKind.GetComponentStatus),
        },
        cancellationToken: cancellation.Token);

    DiscoveryReply discovery = await client.DiscoverAsync(
        new DiscoveryRequest
        {
            Metadata = CreateMetadata(callerSid, CommandKind.DiscoverSystem),
            MaximumItems = maximumItems,
        },
        cancellationToken: cancellation.Token);

    Console.WriteLine(
        $"{label}: {status.State}; protocol {status.ProtocolVersion}; "
        + $"{GetTotalCount(discovery.TotalProcessCount, discovery.Processes.Count)} "
        + $"processes ({discovery.Processes.Count} returned); "
        + $"{GetTotalCount(discovery.TotalServiceCount, discovery.Services.Count)} "
        + $"services ({discovery.Services.Count} returned); "
        + $"truncated: {discovery.Truncated}; pipe: {pipeName}");

    if (string.Equals(
        label,
        "SessionHost",
        StringComparison.OrdinalIgnoreCase))
    {
        GameShiftSessions.GameShiftSessionsClient sessionClient =
            new(channel);
        SessionStateReply session =
            await sessionClient.GetActiveSessionAsync(
                new GetActiveSessionRequest
                {
                    Metadata = CreateMetadata(
                        callerSid,
                        CommandKind.GetActiveSession),
                },
                cancellationToken: cancellation.Token);
        Console.WriteLine(
            session.HasSession
                ? $"SessionHost session: {session.State}; "
                    + $"game: {session.GameDisplayName}; "
                    + $"FPS: {(session.HasFramesPerSecond ? session.FramesPerSecond.ToString("F1", CultureInfo.InvariantCulture) : "brak")}; "
                    + $"frametime: {(session.HasFrameTimeMilliseconds ? session.FrameTimeMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + " ms" : "brak")}; "
                    + $"source PID: {(session.HasFrameRateProcessId ? session.FrameRateProcessId.ToString(CultureInfo.InvariantCulture) : "brak")}; "
                    + $"status: {session.FrameRateStatus}"
                : "SessionHost session: none");
    }
}

static int GetTotalCount(int reportedTotal, int returnedCount) =>
    reportedTotal > 0
        ? reportedTotal
        : returnedCount;

static async Task ProbeSecurityAsync(
    string label,
    string pipeName,
    string callerSid,
    TimeSpan timeout)
{
    using GrpcChannel channel = NamedPipeGrpcChannelFactory.Create(pipeName);
    GameShiftDiagnostics.GameShiftDiagnosticsClient client = new(channel);
    using CancellationTokenSource cancellation = new(timeout);

    RpcRequestMetadata wrongVersion = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    wrongVersion.ProtocolVersion = ProtocolInfo.CurrentVersion + 1;
    await ExpectRejectedAsync(
        $"{label}: unsupported protocol",
        StatusCode.FailedPrecondition,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = wrongVersion },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    RpcRequestMetadata wrongSid = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    wrongSid.CallerSid = $"{callerSid}-999";
    await ExpectRejectedAsync(
        $"{label}: caller SID mismatch",
        StatusCode.PermissionDenied,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = wrongSid },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    RpcRequestMetadata staleTimestamp = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    staleTimestamp.TimestampUnixMilliseconds =
        (DateTimeOffset.UtcNow
            - ProtocolInfo.MaximumClockSkew
            - TimeSpan.FromSeconds(1))
        .ToUnixTimeMilliseconds();
    await ExpectRejectedAsync(
        $"{label}: stale timestamp",
        StatusCode.FailedPrecondition,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = staleTimestamp },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    RpcRequestMetadata malformed = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    malformed.RequestId = "not-a-guid";
    await ExpectRejectedAsync(
        $"{label}: malformed request ID",
        StatusCode.InvalidArgument,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = malformed },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    RpcRequestMetadata wrongCommand = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    wrongCommand.Command =
        (RpcCommandKind)(int)CommandKind.DiscoverSystem;
    await ExpectRejectedAsync(
        $"{label}: command mismatch",
        StatusCode.InvalidArgument,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = wrongCommand },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    StatusRequest replay = new()
    {
        Metadata = CreateMetadata(
            callerSid,
            CommandKind.GetComponentStatus),
    };
    _ = await client.GetStatusAsync(
        replay,
        cancellationToken: cancellation.Token);
    await ExpectRejectedAsync(
        $"{label}: replay",
        StatusCode.AlreadyExists,
        () => client.GetStatusAsync(
                replay,
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    await ExpectRejectedAsync(
        $"{label}: discovery limit",
        StatusCode.InvalidArgument,
        () => client.DiscoverAsync(
                new DiscoveryRequest
                {
                    Metadata = CreateMetadata(
                        callerSid,
                        CommandKind.DiscoverSystem),
                    MaximumItems = 501,
                },
                cancellationToken: cancellation.Token)
            .ResponseAsync);

    RpcRequestMetadata oversized = CreateMetadata(
        callerSid,
        CommandKind.GetComponentStatus);
    oversized.Nonce = new string(
        'A',
        ProtocolInfo.MaximumMessageBytes);
    await ExpectRejectedAsync(
        $"{label}: oversized message",
        StatusCode.ResourceExhausted,
        () => client.GetStatusAsync(
                new StatusRequest { Metadata = oversized },
                cancellationToken: cancellation.Token)
            .ResponseAsync);
}

static async Task ExpectRejectedAsync(
    string scenario,
    StatusCode expectedStatus,
    Func<Task> operation)
{
    try
    {
        await operation();
    }
    catch (RpcException exception)
        when (exception.StatusCode == expectedStatus)
    {
        Console.WriteLine(
            $"{scenario}: rejected with {expectedStatus}");
        return;
    }
    catch (RpcException exception)
    {
        throw new InvalidOperationException(
            $"{scenario}: expected {expectedStatus}, "
            + $"received {exception.StatusCode}.",
            exception);
    }

    throw new InvalidOperationException(
        $"{scenario}: the invalid request was accepted.");
}

static RpcRequestMetadata CreateMetadata(
    string callerSid,
    CommandKind command)
{
    RequestMetadata metadata = new(
        ProtocolInfo.CurrentVersion,
        Guid.NewGuid(),
        sessionId: null,
        callerSid,
        DateTimeOffset.UtcNow,
        command,
        RandomNumberGenerator.GetHexString(32),
        IdempotencyKey.Create());

    return RpcRequestMetadataMapper.ToRpc(metadata);
}

static int ReadMaximumItems(string[] arguments)
{
    string? value = ReadOption(arguments, "--maximum-items");
    if (value is null)
    {
        return defaultMaximumItems;
    }

    if (!int.TryParse(value, out int maximumItems)
        || maximumItems is <= 0 or > 500)
    {
        throw new ArgumentException(
            "--maximum-items must be an integer between 1 and 500.");
    }

    return maximumItems;
}

static string? ReadOption(string[] arguments, string option)
{
    int index = Array.FindIndex(
        arguments,
        argument => string.Equals(
            argument,
            option,
            StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    if (index == arguments.Length - 1)
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return arguments[index + 1];
}

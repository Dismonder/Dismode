using System.Diagnostics;
using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Windows.Ipc;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameShift.UI.Services;

public sealed class DiagnosticsProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServiceDiscoveryTimeout =
        TimeSpan.FromSeconds(10);
    private const int MaximumDiscoveryItems = 500;

    private readonly string _callerSid;

    public DiagnosticsProbeService(string callerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        _callerSid = callerSid.Trim();
    }

    public async Task<DiagnosticsEndpointSnapshot> ProbeAsync(
        string component,
        string pipeName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        Stopwatch stopwatch = Stopwatch.StartNew();
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using GrpcChannel channel =
                NamedPipeGrpcChannelFactory.Create(pipeName);
            GameShiftDiagnostics.GameShiftDiagnosticsClient client =
                new(channel);
            StatusReply status = await client.GetStatusAsync(
                new StatusRequest
                {
                    Metadata = CreateMetadata(
                        CommandKind.GetComponentStatus),
                },
                cancellationToken: timeout.Token);
            DiscoveryReply discovery = await client.DiscoverAsync(
                new DiscoveryRequest
                {
                    Metadata = CreateMetadata(CommandKind.DiscoverSystem),
                    MaximumItems = MaximumDiscoveryItems,
                },
                cancellationToken: timeout.Token);

            return new(
                component,
                IsAvailable: true,
                IsCompatible:
                    status.ProtocolVersion == ProtocolInfo.CurrentVersion,
                status.State,
                status.Message,
                status.ProtocolVersion,
                GetTotalCount(
                    discovery.TotalProcessCount,
                    discovery.Processes.Count),
                GetTotalCount(
                    discovery.TotalServiceCount,
                    discovery.Services.Count),
                discovery.Truncated,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(
                component,
                "Przekroczono limit czasu połączenia.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (RpcException exception)
            when (exception.StatusCode is
                StatusCode.Unavailable
                or StatusCode.DeadlineExceeded
                or StatusCode.Cancelled
                or StatusCode.Internal)
        {
            return Unavailable(
                component,
                "Host nie nasłuchuje na chronionym pipe.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return Unavailable(
                component,
                "Nie można otworzyć chronionego pipe.",
                stopwatch.ElapsedMilliseconds);
        }
        catch (IOException)
        {
            return Unavailable(
                component,
                "Połączenie z pipe zostało przerwane.",
                stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<DiscoveredProcessClientSnapshot>>
        DiscoverProcessesAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            using GrpcChannel channel =
                NamedPipeGrpcChannelFactory.Create(
                    PipeNames.ForUser(_callerSid));
            GameShiftDiagnostics.GameShiftDiagnosticsClient client =
                new(channel);
            DiscoveryReply discovery = await client.DiscoverAsync(
                new DiscoveryRequest
                {
                    Metadata = CreateMetadata(
                        CommandKind.DiscoverSystem),
                    MaximumItems = MaximumDiscoveryItems,
                },
                cancellationToken: timeout.Token);

            return discovery.Processes
                .Select(process =>
                    new DiscoveredProcessClientSnapshot(
                        process.ProcessId,
                        process.Name,
                        process.StartedUnixMilliseconds == 0
                            ? null
                            : DateTimeOffset.FromUnixTimeMilliseconds(
                                process.StartedUnixMilliseconds),
                        process.ExecutablePath,
                        process.SessionId,
                        process.WorkingSetBytes,
                        TimeSpan.FromMilliseconds(
                            process.TotalProcessorTimeMilliseconds),
                        process.HasMainWindow,
                        process.SafetyClassification,
                        process.ClassificationReason,
                        process.RecommendedAction,
                        process.PriorityClass))
                .ToArray();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Analiza aplikacji w tle przekroczyła limit czasu.");
        }
        catch (RpcException exception)
        {
            throw new IOException(
                "SessionHost nie udostępnił listy procesów.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new IOException(
                "Nie można połączyć się z SessionHost.",
                exception);
        }
    }

    public async Task<IReadOnlyList<DiscoveredServiceClientSnapshot>>
        DiscoverServicesAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ServiceDiscoveryTimeout);

        try
        {
            using GrpcChannel channel =
                NamedPipeGrpcChannelFactory.Create(PipeNames.System);
            GameShiftDiagnostics.GameShiftDiagnosticsClient client =
                new(channel);
            DiscoveryReply discovery = await client.DiscoverAsync(
                new DiscoveryRequest
                {
                    Metadata = CreateMetadata(
                        CommandKind.DiscoverSystem),
                    MaximumItems = MaximumDiscoveryItems,
                },
                cancellationToken: timeout.Token);

            return discovery.Services
                .Select(service =>
                    new DiscoveredServiceClientSnapshot(
                        service.ServiceName,
                        service.DisplayName,
                        service.Status,
                        service.CanStop,
                        service.ServiceType,
                        service.Dependencies.ToArray(),
                        service.StartType,
                        service.SafetyClassification,
                        service.ClassificationReason,
                        service.RecommendedAction,
                        service.IsDriver,
                        service.IsSharedProcess,
                        service.TriggerCount,
                        service.RunningDependentServices.ToArray()))
                .ToArray();
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Analiza usług przekroczyła limit czasu.");
        }
        catch (RpcException exception)
        {
            throw new IOException(
                "SystemAgent nie udostępnił listy usług.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new IOException(
                "Nie można połączyć się z SystemAgent.",
                exception);
        }
    }

    private RpcRequestMetadata CreateMetadata(CommandKind command)
    {
        RequestMetadata metadata = new(
            ProtocolInfo.CurrentVersion,
            Guid.NewGuid(),
            sessionId: null,
            _callerSid,
            DateTimeOffset.UtcNow,
            command,
            RandomNumberGenerator.GetHexString(32),
            IdempotencyKey.Create());
        return RpcRequestMetadataMapper.ToRpc(metadata);
    }

    private static int GetTotalCount(
        int reportedTotal,
        int returnedCount) =>
        reportedTotal > 0
            ? reportedTotal
            : returnedCount;

    private static DiagnosticsEndpointSnapshot Unavailable(
        string component,
        string message,
        long latencyMilliseconds) =>
        new(
            component,
            IsAvailable: false,
            IsCompatible: false,
            State: "Offline",
            message,
            ProtocolVersion: 0,
            ProcessCount: 0,
            ServiceCount: 0,
            IsTruncated: false,
            latencyMilliseconds);
}

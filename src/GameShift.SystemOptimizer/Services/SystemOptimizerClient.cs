using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Windows.Ipc;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameShift.SystemOptimizer.Services;

internal sealed class SystemOptimizerClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly string _callerSid;
    private readonly GrpcChannel _channel;
    private readonly GameShiftSystemOptimizer.GameShiftSystemOptimizerClient _client;
    private bool _disposed;

    internal SystemOptimizerClient(string callerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        _callerSid = callerSid.Trim();
        _channel = NamedPipeGrpcChannelFactory.Create(PipeNames.System);
        _client = new(_channel);
    }

    internal Task<SystemOptimizerStatusReply> GetStatusAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.GetStatusAsync(
                new()
                {
                    Metadata = CreateMetadata(CommandKind.GetSystemOptimizerStatus),
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemOptimizerHardwareReply> GetHardwareAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.GetHardwareAsync(
                new()
                {
                    Metadata = CreateMetadata(CommandKind.GetHardwareFingerprint),
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemTweakCatalogReply> GetCatalogAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.GetCatalogAsync(
                new()
                {
                    Metadata = CreateMetadata(CommandKind.GetSystemTweakCatalog),
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemOptimizationHistoryReply> GetHistoryAsync(
        int maximumItems,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.GetHistoryAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.GetSystemOptimizationHistory),
                    MaximumItems = Math.Clamp(maximumItems, 1, 200),
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemOptimizationProfilesReply> GetProfilesAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.GetProfilesAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.GetSystemOptimizationProfiles),
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemOptimizerOperationReply> SetConsentAsync(
        bool accepted,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.SetConsentAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.SetSystemOptimizerConsent),
                    Accepted = accepted,
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemOptimizerOperationReply> RestoreAllAsync(
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.RestoreAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.RestoreSystemOptimization),
                    Target = SystemRestoreTarget.AllGameshiftChanges,
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<SystemExperimentReply> PrepareExperimentAsync(
        string gameProfileId,
        string executableSha256,
        string hardwareFingerprintHash,
        SystemTweakDefinitionMessage definition,
        string? dangerousConfirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.AllowedValues.Count == 0)
        {
            throw new ArgumentException(
                "Wybrana pozycja nie udostępnia dozwolonej wartości.",
                nameof(definition));
        }

        return ExecuteAsync(
            token => _client.PrepareExperimentAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.PrepareSystemExperiment),
                    GameProfileId = gameProfileId,
                    GameExecutableSha256 = executableSha256,
                    HardwareFingerprintHash = hardwareFingerprintHash,
                    Selection = new()
                    {
                        TweakId = definition.Id,
                        Revision = definition.Revision,
                        Value = definition.AllowedValues[0],
                        DangerousConfirmation =
                            dangerousConfirmation ?? string.Empty,
                    },
                },
                cancellationToken: token),
            cancellationToken);
    }

    internal Task<SystemExperimentReply> AdvanceExperimentAsync(
        string experimentId,
        ExperimentControlAction action,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            token => _client.AdvanceExperimentAsync(
                new()
                {
                    Metadata = CreateMetadata(
                        CommandKind.AdvanceSystemExperiment),
                    ExperimentId = experimentId,
                    Action = action,
                },
                cancellationToken: token),
            cancellationToken);

    internal Task<BenchmarkDecisionReply> SubmitCaptureAsync(
        string experimentId,
        BenchmarkVariantValue variant,
        DateTimeOffset startedAtUtc,
        TimeSpan stabilizationDuration,
        TimeSpan measurementDuration,
        IReadOnlyList<double> frameTimesMilliseconds,
        double? averageCpuPercent,
        double? averageGpuBusyPercent,
        long? averageUsedRamBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMilliseconds);
        SubmitBenchmarkCaptureRequest request = new()
        {
            Metadata = CreateMetadata(CommandKind.SubmitBenchmarkCapture),
            ExperimentId = experimentId,
            CaptureId = Guid.NewGuid().ToString("D"),
            Variant = variant,
            StartedUnixMilliseconds = startedAtUtc.ToUnixTimeMilliseconds(),
            StabilizationMilliseconds = checked((int)stabilizationDuration.TotalMilliseconds),
            MeasurementMilliseconds = checked((int)measurementDuration.TotalMilliseconds),
        };
        request.FrameTimesMilliseconds.AddRange(frameTimesMilliseconds);
        if (averageCpuPercent is double cpuPercent)
        {
            request.AverageCpuPercent = cpuPercent;
        }

        if (averageGpuBusyPercent is double gpuBusyPercent)
        {
            request.AverageGpuBusyPercent = gpuBusyPercent;
        }

        if (averageUsedRamBytes is long usedRamBytes)
        {
            request.AverageUsedRamBytes = usedRamBytes;
        }

        return ExecuteAsync(
            token => _client.SubmitCaptureAsync(
                request,
                cancellationToken: token),
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _channel.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private RpcRequestMetadata CreateMetadata(CommandKind command) =>
        RpcRequestMetadataMapper.ToRpc(
            new(
                ProtocolInfo.CurrentVersion,
                Guid.NewGuid(),
                sessionId: null,
                _callerSid,
                DateTimeOffset.UtcNow,
                command,
                RandomNumberGenerator.GetHexString(32),
                IdempotencyKey.Create()));

    private static async Task<TReply> ExecuteAsync<TReply>(
        Func<CancellationToken, AsyncUnaryCall<TReply>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await operation(timeout.Token).ResponseAsync
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (
            timeout.IsCancellationRequested
            && exception is OperationCanceledException or RpcException)
        {
            throw new TimeoutException(
                "Usługa System Optimizer nie odpowiedziała w wymaganym czasie.",
                exception);
        }
    }
}

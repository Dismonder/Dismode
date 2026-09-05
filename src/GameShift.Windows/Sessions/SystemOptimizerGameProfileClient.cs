using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Ipc;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameShift.Windows.Sessions;

public sealed class SystemOptimizerGameProfileClient :
    ISystemGameProfileCoordinator,
    IDisposable
{
    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(20);

    private readonly string _callerSid;
    private readonly GrpcChannel _channel;
    private readonly GameShiftSystemOptimizer.GameShiftSystemOptimizerClient
        _client;
    private bool _disposed;

    public SystemOptimizerGameProfileClient(string callerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerSid);
        _callerSid = callerSid.Trim();
        _channel = NamedPipeGrpcChannelFactory.Create(PipeNames.System);
        _client = new(_channel);
    }

    public async ValueTask<SystemGameProfileOperationResult> ActivateAsync(
        GameProfileId profileId,
        ProcessIdentity gameIdentity,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(gameIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        ActivatePerGameOptimizationProfileRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.ActivatePerGameOptimizationProfile),
            GameProfileId = profileId.Value.ToString("D"),
            GameExecutableSha256 = gameIdentity.ExecutableSha256,
        };
        try
        {
            SystemOptimizerOperationReply reply = await ExecuteAsync(
                    token => _client.ActivatePerGameProfileAsync(
                        request,
                        cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);
            return reply.Succeeded
                ? SystemGameProfileOperationResult.Applied(reply.Message)
                : SystemGameProfileOperationResult.Failed(reply.Message);
        }
        catch (RpcException exception)
            when (exception.StatusCode == StatusCode.NotFound)
        {
            return SystemGameProfileOperationResult.Skipped(
                "Dla tej gry nie zapisano profilu System Optimizer.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return SystemGameProfileOperationResult.Failed(
                "Nie zastosowano profilu System Optimizer: "
                + GetFailureMessage(exception));
        }
    }

    public async ValueTask<SystemGameProfileOperationResult> RestoreAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RestorePerGameOptimizationProfileRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.RestorePerGameOptimizationProfile),
        };
        try
        {
            SystemOptimizerOperationReply reply = await ExecuteAsync(
                    token => _client.RestorePerGameProfileAsync(
                        request,
                        cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);
            return reply.Succeeded
                ? SystemGameProfileOperationResult.Restored(reply.Message)
                : SystemGameProfileOperationResult.Failed(reply.Message);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return SystemGameProfileOperationResult.Failed(
                "Nie przywrócono profilu System Optimizer: "
                + GetFailureMessage(exception));
        }
    }

    public async ValueTask<SystemGameProfileOperationResult> PrepareForUpdateAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        PrepareSystemOptimizerUpdateRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.PrepareSystemOptimizerUpdate),
        };
        try
        {
            SystemOptimizerOperationReply reply = await ExecuteAsync(
                    token => _client.PrepareForUpdateAsync(
                        request,
                        cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);
            return reply.Succeeded
                ? SystemGameProfileOperationResult.Restored(reply.Message)
                : SystemGameProfileOperationResult.Failed(reply.Message);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return SystemGameProfileOperationResult.Failed(
                "Nie przygotowano System Optimizer do aktualizacji: "
                + GetFailureMessage(exception));
        }
    }

    public async ValueTask<SystemGameProfileOperationResult> RestoreAllAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        RestoreSystemOptimizationRequest request = new()
        {
            Metadata = CreateMetadata(
                CommandKind.RestoreSystemOptimization),
            Target = SystemRestoreTarget.AllGameshiftChanges,
        };
        try
        {
            SystemOptimizerOperationReply reply = await ExecuteAsync(
                    token => _client.RestoreAsync(
                        request,
                        cancellationToken: token),
                    cancellationToken)
                .ConfigureAwait(false);
            return reply.Succeeded
                ? SystemGameProfileOperationResult.Restored(reply.Message)
                : SystemGameProfileOperationResult.Failed(reply.Message);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return SystemGameProfileOperationResult.Failed(
                "Nie przywrócono zmian System Optimizer: "
                + GetFailureMessage(exception));
        }
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

    private static async Task<SystemOptimizerOperationReply> ExecuteAsync(
        Func<CancellationToken, AsyncUnaryCall<SystemOptimizerOperationReply>>
            operation,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        return await operation(timeout.Token).ResponseAsync
            .ConfigureAwait(false);
    }

    private static bool IsRecoverableFailure(Exception exception) =>
        exception is RpcException
            or HttpRequestException
            or IOException
            or OperationCanceledException;

    private static string GetFailureMessage(Exception exception) =>
        exception is RpcException rpc
            && !string.IsNullOrWhiteSpace(rpc.Status.Detail)
                ? rpc.Status.Detail
                : exception.Message;
}

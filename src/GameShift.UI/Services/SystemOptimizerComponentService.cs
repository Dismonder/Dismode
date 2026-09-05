using System.Diagnostics;
using System.Security.Cryptography;
using GameShift.Contracts.Commands;
using GameShift.Contracts.Grpc;
using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Windows.Ipc;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameShift.UI.Services;

public sealed class SystemOptimizerComponentService : IDisposable
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(5);
    private readonly string _userSid;
    private readonly string _installationRoot;
    private readonly string _executablePath;
    private readonly GrpcChannel _channel;
    private readonly GameShiftSystemOptimizer.GameShiftSystemOptimizerClient
        _client;
    private bool _disposed;

    public SystemOptimizerComponentService(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        _userSid = userSid.Trim();
        _installationRoot = Path.GetFullPath(AppContext.BaseDirectory);
        string componentPath = Path.Combine(
            _installationRoot,
            "SystemOptimizer",
            "GameShift.SystemOptimizer.exe");
        string rootPath = Path.Combine(
            _installationRoot,
            "GameShift.SystemOptimizer.exe");
        _executablePath = File.Exists(componentPath)
            ? Path.GetFullPath(componentPath)
            : Path.GetFullPath(rootPath);
        _channel = NamedPipeGrpcChannelFactory.Create(PipeNames.System);
        _client = new(_channel);
    }

    public async Task<SystemOptimizerComponentSnapshot> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool executableExists = File.Exists(_executablePath);
        if (!executableExists)
        {
            return SystemOptimizerComponentPresentation.CreateSnapshot(
                executableExists: false,
                serviceReachable: false,
                isReadOnly: true,
                isRecoveryClean: true,
                activeGameProfileId: null,
                activeExperimentId: null,
                serviceMessage: null);
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatusTimeout);
        try
        {
            SystemOptimizerStatusReply status = await _client.GetStatusAsync(
                    new()
                    {
                        Metadata = CreateMetadata(
                            CommandKind.GetSystemOptimizerStatus),
                    },
                    cancellationToken: timeout.Token)
                .ResponseAsync
                .ConfigureAwait(false);
            return SystemOptimizerComponentPresentation.CreateSnapshot(
                executableExists: true,
                serviceReachable: status.IsServiceReady,
                status.IsReadOnly,
                status.Recovery.IsJournalClean,
                status.ActiveGameProfileId,
                status.ActiveExperimentId,
                status.Message);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            return SystemOptimizerComponentPresentation.CreateSnapshot(
                executableExists: true,
                serviceReachable: false,
                isReadOnly: true,
                isRecoveryClean: true,
                activeGameProfileId: null,
                activeExperimentId: null,
                serviceMessage: exception.Message);
        }
    }

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_executablePath))
        {
            throw new FileNotFoundException(
                "System Optimizer nie jest zainstalowany.",
                _executablePath);
        }

        string? parent = Path.GetDirectoryName(_executablePath);
        if (parent is null || !IsInsideInstallationRoot(_executablePath))
        {
            throw new InvalidOperationException(
                "Ścieżka System Optimizer opuściła katalog instalacji.");
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = parent,
            UseShellExecute = false,
        });
        if (process is null)
        {
            throw new InvalidOperationException(
                "Windows nie uruchomił System Optimizer.");
        }

        return Task.CompletedTask;
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

    private bool IsInsideInstallationRoot(string path)
    {
        string normalizedRoot = _installationRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private RpcRequestMetadata CreateMetadata(CommandKind command) =>
        RpcRequestMetadataMapper.ToRpc(
            new(
                ProtocolInfo.CurrentVersion,
                Guid.NewGuid(),
                sessionId: null,
                _userSid,
                DateTimeOffset.UtcNow,
                command,
                RandomNumberGenerator.GetHexString(32),
                IdempotencyKey.Create()));

    private static bool IsConnectionFailure(Exception exception) =>
        exception is RpcException
            or IOException
            or HttpRequestException
            or OperationCanceledException;
}

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Dismode.Core.SystemOptimization;
using Dismode.Windows.Processes;
using Dismode.Windows.Security;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;

namespace Dismode.SystemAgent.Security;

internal sealed partial class NamedPipeCallerIdentityAccessor
{
    private readonly ProcessIdentityProvider _processIdentityProvider = new();
    private readonly AuthenticodeSignatureVerifier _signatureVerifier;

    internal NamedPipeCallerIdentityAccessor(
        AuthenticodeSignatureVerifier signatureVerifier)
    {
        _signatureVerifier = signatureVerifier;
    }

    internal async ValueTask<SystemOptimizerCallerIdentity> CaptureAsync(
        ServerCallContext context,
        CancellationToken cancellationToken)
    {
        IConnectionNamedPipeFeature? feature = context
            .GetHttpContext()
            .Features
            .Get<IConnectionNamedPipeFeature>();
        NamedPipeServerStream pipe = feature?.NamedPipe
            ?? throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "The call is not associated with a named-pipe client."));

        string actualSid = ReadImpersonatedSid(pipe);
        int processId = ReadClientProcessId(pipe);
        Dismode.Core.Domain.Processes.ProcessIdentity? processIdentity =
            await _processIdentityProvider.TryCaptureAsync(
                    processId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (processIdentity is null
            || !StringComparer.OrdinalIgnoreCase.Equals(
                processIdentity.UserSid,
                actualSid))
        {
            throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "The pipe identity does not match the caller process token."));
        }

        AuthenticodeVerificationResult signature =
            _signatureVerifier.Verify(processIdentity.ExecutablePath);
        if (ReadClientProcessId(pipe) != processId
            || !await _processIdentityProvider.MatchesRuntimeIdentityAsync(
                    processIdentity,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "The caller identity changed during authorization."));
        }

        return new(
            actualSid,
            processId,
            processIdentity.ExecutablePath,
            signature.HasValidAuthenticodeSignature,
            signature.IsTrustedSigner);
    }

    private static string ReadImpersonatedSid(NamedPipeServerStream pipe)
    {
        string? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent(
                    TokenAccessLevels.Query);
                sid = identity.User?.Value;
            });
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "Windows could not impersonate the pipe client."));
        }

        return sid
            ?? throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "The pipe client token does not contain a SID."));
    }

    private static int ReadClientProcessId(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out uint processId)
            || processId is 0 or > int.MaxValue)
        {
            throw new RpcException(
                new Status(
                    StatusCode.Unauthenticated,
                    "Windows did not return a valid named-pipe client PID."));
        }

        return checked((int)processId);
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetNamedPipeClientProcessId",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Dismode.MemoryOptimizer.Core.Ipc;

namespace Dismode.MemoryService;

internal sealed class MemoryPipeServer
{
    private static readonly TimeSpan RequestReadTimeout =
        TimeSpan.FromSeconds(10);

    private readonly string _userSid;
    private readonly MemoryUserRuntime _runtime;
    private readonly ReplayProtector _replayProtector = new();

    internal MemoryPipeServer(string userSid, MemoryUserRuntime runtime)
    {
        _userSid = userSid;
        _runtime = runtime;
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(
                    cancellationToken).ConfigureAwait(false);
                await HandleConnectionAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                // A single bad request must not stop the pipe loop.
                Trace.TraceWarning(
                    "Memory Optimizer pipe request failed: {0}",
                    exception.Message);
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        // An idle or half-sent client must not block every later request.
        using CancellationTokenSource readTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(RequestReadTimeout);
        MemoryOptimizerRequest request = await ProtocolCodec.ReadAsync<
            MemoryOptimizerRequest>(pipe, readTimeout.Token).ConfigureAwait(false);

        string? actualSid = null;
        pipe.RunAsClient(() =>
        {
            using WindowsIdentity? identity = WindowsIdentity.GetCurrent(true);
            actualSid = identity?.User?.Value;
        });

        MemoryOptimizerResponse response;
        if (string.IsNullOrWhiteSpace(actualSid))
        {
            response = Failure(
                request,
                "identity-unavailable",
                "The service could not authenticate the pipe client.");
        }
        else
        {
            ProtocolValidationResult validation = _replayProtector.Validate(
                request,
                actualSid,
                DateTimeOffset.UtcNow);
            if (!validation.IsValid)
            {
                response = Failure(
                    request,
                    validation.Code.ToString(),
                    validation.Message);
            }
            else if (validation.CachedResponse is not null)
            {
                response = validation.CachedResponse with
                {
                    RequestId = request.RequestId,
                };
            }
            else
            {
                response = await _runtime.HandleAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                _replayProtector.StoreResponse(
                    actualSid,
                    request,
                    response,
                    DateTimeOffset.UtcNow);
            }
        }

        await ProtocolCodec.WriteAsync(
            pipe,
            response,
            cancellationToken).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreatePipe()
    {
        SecurityIdentifier user = new(_userSid);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddRule(
            security,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl);
        AddRule(
            security,
            new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid,
                null),
            PipeAccessRights.FullControl);
        AddRule(
            security,
            user,
            PipeAccessRights.ReadWrite);

        return NamedPipeServerStreamAcl.Create(
            MemoryOptimizerProtocol.CreatePipeName(_userSid),
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            MemoryOptimizerProtocol.MaximumMessageBytes,
            MemoryOptimizerProtocol.MaximumMessageBytes,
            security,
            HandleInheritability.None,
            PipeAccessRights.ChangePermissions);
    }

    private static void AddRule(
        PipeSecurity security,
        SecurityIdentifier sid,
        PipeAccessRights rights) =>
        security.AddAccessRule(new(
            sid,
            rights,
            AccessControlType.Allow));

    private static MemoryOptimizerResponse Failure(
        MemoryOptimizerRequest request,
        string code,
        string message) =>
        new(
            MemoryOptimizerProtocol.Version,
            request.RequestId,
            false,
            code,
            message,
            "{}");
}

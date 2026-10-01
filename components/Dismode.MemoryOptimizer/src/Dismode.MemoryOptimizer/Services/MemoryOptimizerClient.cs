using System.IO.Pipes;
using System.Security.Principal;
using Dismode.MemoryOptimizer.Core.Ipc;

namespace Dismode.MemoryOptimizer.Services;

internal sealed class MemoryOptimizerClient
{
    private readonly string _userSid;

    internal MemoryOptimizerClient()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value ??
            throw new InvalidOperationException(
                "Current Windows user does not have a SID.");
    }

    internal async Task<TReply> SendAsync<TPayload, TReply>(
        MemoryOptimizerCommand command,
        TPayload payload,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        MemoryOptimizerRequest request = MemoryOptimizerProtocol.CreateRequest(
            _userSid,
            command,
            payload,
            idempotencyKey);
        await using NamedPipeClientStream pipe = new(
            ".",
            MemoryOptimizerProtocol.CreatePipeName(_userSid),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            TokenImpersonationLevel.Impersonation);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await ProtocolCodec.WriteAsync(
            pipe,
            request,
            timeout.Token).ConfigureAwait(false);
        MemoryOptimizerResponse response = await ProtocolCodec.ReadAsync<
            MemoryOptimizerResponse>(pipe, timeout.Token).ConfigureAwait(false);
        MemoryOptimizerProtocol.ValidateResponse(request, response);
        if (!response.Succeeded)
        {
            throw new InvalidOperationException(
                $"{response.Code}: {response.Message}");
        }

        return MemoryOptimizerProtocol.DeserializePayload<TReply>(
                response.PayloadJson) ??
            throw new InvalidDataException("Service returned an empty payload.");
    }
}

using System.IO.Pipes;
using System.Net.Http;
using System.Security.Principal;

namespace GameShift.Windows.Ipc;

public sealed class NamedPipeConnectionFactory
{
    private readonly string _pipeName;

    public NamedPipeConnectionFactory(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext connectionContext,
        CancellationToken cancellationToken = default)
    {
        NamedPipeClientStream clientStream = new(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.WriteThrough | PipeOptions.Asynchronous,
            impersonationLevel: TokenImpersonationLevel.Identification);

        try
        {
            await clientStream.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return clientStream;
        }
        catch
        {
            await clientStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

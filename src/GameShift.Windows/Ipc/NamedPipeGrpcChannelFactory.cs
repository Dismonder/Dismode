using Grpc.Net.Client;

namespace GameShift.Windows.Ipc;

public static class NamedPipeGrpcChannelFactory
{
    public static GrpcChannel Create(string pipeName)
    {
        NamedPipeConnectionFactory connectionFactory = new(pipeName);
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = connectionFactory.ConnectAsync,
            EnableMultipleHttp2Connections = false,
        };

        return GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                MaxReceiveMessageSize = 1024 * 1024,
                MaxSendMessageSize = 1024 * 1024,
            });
    }
}


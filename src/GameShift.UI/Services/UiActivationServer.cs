using System.Buffers.Binary;
using System.IO.Pipes;
using GameShift.Core.Activation;

namespace GameShift.UI.Services;

public sealed class UiActivationServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Action<UiActivationRequest> _requestAccepted;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Guid> _recentRequestIds = [];
    private readonly Queue<Guid> _recentRequestOrder = new();
    private readonly Task _serverTask;
    private bool _disposed;

    public UiActivationServer(
        string userSid,
        Action<UiActivationRequest> requestAccepted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        _requestAccepted = requestAccepted
            ?? throw new ArgumentNullException(nameof(requestAccepted));
        _pipeName = UiActivationProtocol.GetPipeName(userSid);
        _serverTask = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            await using NamedPipeServerStream pipe = new(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token)
                    .ConfigureAwait(false);
                byte[] lengthBytes = new byte[sizeof(int)];
                await pipe.ReadExactlyAsync(
                        lengthBytes,
                        _lifetime.Token)
                    .ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(
                    lengthBytes);
                if (length is <= 0
                    or > UiActivationProtocol.MaximumMessageBytes)
                {
                    throw new InvalidDataException(
                        "Żądanie aktywacji przekracza limit.");
                }

                byte[] payload = new byte[length];
                await pipe.ReadExactlyAsync(payload, _lifetime.Token)
                    .ConfigureAwait(false);
                UiActivationRequest request =
                    UiActivationProtocol.DeserializeAndValidate(
                        payload,
                        DateTimeOffset.UtcNow);
                if (!RememberRequest(request.RequestId))
                {
                    throw new InvalidDataException(
                        "Powtórzone żądanie aktywacji zostało odrzucone.");
                }

                _requestAccepted(request);
                pipe.WriteByte(1);
                await pipe.FlushAsync(_lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException
                    or InvalidDataException
                    or UnauthorizedAccessException)
            {
                TryReject(pipe);
            }
        }
    }

    private bool RememberRequest(Guid requestId)
    {
        lock (_recentRequestIds)
        {
            if (!_recentRequestIds.Add(requestId))
            {
                return false;
            }

            _recentRequestOrder.Enqueue(requestId);
            while (_recentRequestOrder.Count > 128)
            {
                _recentRequestIds.Remove(_recentRequestOrder.Dequeue());
            }

            return true;
        }
    }

    private static void TryReject(NamedPipeServerStream pipe)
    {
        try
        {
            if (pipe.IsConnected)
            {
                pipe.WriteByte(0);
                pipe.Flush();
            }
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lifetime.Cancel();
        try
        {
            _serverTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

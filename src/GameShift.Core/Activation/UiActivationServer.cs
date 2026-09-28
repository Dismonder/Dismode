using System.Buffers.Binary;
using System.IO.Pipes;

namespace GameShift.Core.Activation;

/// <summary>
/// Rura, na ktorej dzialajace okno GameShift przyjmuje zadania od drugiego
/// egzemplarza (uruchom gre, pokaz sie). Jedno polaczenie naraz, kazde z
/// limitem czasu: klient, ktory polaczyl sie i zamilkl, nie moze na zawsze
/// zablokowac jedynego wejscia. Rura powstaje wewnatrz petli obslugi, wiec
/// jej chwilowa niedostepnosc (cudzy proces zajal nazwe) konczy sie ponowna
/// proba, a nie cicha smiercia serwera na reszte zycia procesu.
/// </summary>
public sealed class UiActivationServer : IDisposable
{
    public static readonly TimeSpan DefaultConnectionDeadline =
        TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly Action<UiActivationRequest> _requestAccepted;
    private readonly TimeSpan _connectionDeadline;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Guid> _recentRequestIds = [];
    private readonly Queue<Guid> _recentRequestOrder = new();
    private readonly Task _serverTask;
    private bool _disposed;

    public UiActivationServer(
        string userSid,
        Action<UiActivationRequest> requestAccepted,
        TimeSpan? connectionDeadline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        _requestAccepted = requestAccepted
            ?? throw new ArgumentNullException(nameof(requestAccepted));
        _connectionDeadline = connectionDeadline ?? DefaultConnectionDeadline;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            _connectionDeadline,
            TimeSpan.Zero);
        _pipeName = UiActivationProtocol.GetPipeName(userSid);
        _serverTask = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances:
                        NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_lifetime.Token)
                    .ConfigureAwait(false);

                using CancellationTokenSource deadline =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token);
                deadline.CancelAfter(_connectionDeadline);
                await ServeAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or InvalidDataException
                    or UnauthorizedAccessException
                    or OperationCanceledException
                    or ObjectDisposedException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                if (pipe is null)
                {
                    if (!await DelayBeforeRetryAsync().ConfigureAwait(false))
                    {
                        return;
                    }
                }
                else if (exception is not OperationCanceledException)
                {
                    TryReject(pipe);
                }
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        byte[] lengthBytes = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(lengthBytes, cancellationToken)
            .ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > UiActivationProtocol.MaximumMessageBytes)
        {
            throw new InvalidDataException(
                "Żądanie aktywacji przekracza limit.");
        }

        byte[] payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, cancellationToken)
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
        // Bez Flush: zapis do rury jest niebuforowany, a Flush to
        // FlushFileBuffers, ktore czeka, az klient odczyta dane - klient,
        // ktory juz nie czyta, zawiesilby serwer na zawsze.
        await pipe.WriteAsync(new byte[] { 1 }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> DelayBeforeRetryAsync()
    {
        try
        {
            await Task.Delay(RetryDelay, _lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromMilliseconds(500));
                pipe.WriteAsync(new byte[] { 0 }, timeout.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (Exception exception) when (
            exception is
                IOException
                or ObjectDisposedException
                or InvalidOperationException
                or OperationCanceledException)
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

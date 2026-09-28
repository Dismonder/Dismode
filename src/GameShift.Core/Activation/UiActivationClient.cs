using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;

namespace GameShift.Core.Activation;

/// <summary>
/// Hands a request to the GameShift window that is already running: start
/// this game, or just come to the front. Used by a second GameShift.UI that
/// lost the single-instance lock, so a double click on the shortcut ends in
/// the existing window instead of a second one.
/// </summary>
public static class UiActivationClient
{
    public static bool TrySend(
        string userSid,
        UiActivationRequest request,
        TimeSpan timeout,
        out string? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentNullException.ThrowIfNull(request);
        failure = null;
        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                UiActivationProtocol.GetPipeName(userSid),
                PipeDirection.InOut,
                PipeOptions.None,
                TokenImpersonationLevel.Identification);
            pipe.Connect((int)Math.Clamp(
                timeout.TotalMilliseconds,
                100,
                int.MaxValue));

            byte[] payload = UiActivationProtocol.Serialize(request);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
            // Bez Flush: zapis do rury jest niebuforowany, a Flush to
            // FlushFileBuffers, ktore czeka na odczyt po drugiej stronie.
            pipe.Write(length);
            pipe.Write(payload);
            if (pipe.ReadByte() == 1)
            {
                return true;
            }

            failure = "Uruchomione GameShift odrzuciło żądanie.";
        }
        catch (Exception exception) when (
            exception is
                IOException
                or TimeoutException
                or InvalidOperationException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            failure = exception.Message;
        }

        return false;
    }
}

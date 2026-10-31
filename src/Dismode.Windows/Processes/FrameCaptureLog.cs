namespace Dismode.Windows.Processes;

/// <summary>
/// What the frame measurement actually did, one line per change, in
/// <c>%LOCALAPPDATA%\Dismode\frame-capture.log</c>.
/// <para>
/// Every recorded game session ended with zero frame samples and nothing
/// said why: the overlay showed a status, the history showed nothing, and the
/// host's standard error is lost when the launcher starts it. This records
/// which processes the session offered, which one was measured, every status
/// change, PresentMon's own error output and, periodically, which frame
/// streams were seen — enough to tell a wrong target from a silent capture
/// from a capture that is being restarted.
/// </para>
/// <para>
/// Lines are written on change only, never per frame. The file is cleared
/// when it outgrows its limit, so the recent sessions — the ones a report is
/// about — are kept. A failed write never costs a measurement.
/// </para>
/// </summary>
internal static class FrameCaptureLog
{
    private const long SizeLimitBytes = 1024 * 1024;
    private static readonly object Sync = new();

    /// <summary>Replaces the log location; tests only.</summary>
    internal static string? PathOverride { get; set; }

    internal static string LogPath =>
        PathOverride
        ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Dismode",
            "frame-capture.log");

    internal static void Write(string message)
    {
        string line =
            $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} "
            + message.ReplaceLineEndings(" ")
            + Environment.NewLine;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
        lock (Sync)
        {
            // Do pliku pisza dwa procesy naraz — nakladka w UI i sesja
            // w SessionHost — a ich wpisy z tej samej chwili sa wlasnie tym,
            // czego dziennik ma dowiesc. Wspoldzielony zapis z jedna ponowna
            // proba zamiast File.AppendAllText, ktory drugiemu procesowi
            // odmawial dostepu i gubil linie.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    AppendShared(LogPath, bytes);
                    return;
                }
                catch (IOException) when (attempt == 0)
                {
                    Thread.Sleep(5);
                }
                catch (Exception exception) when (
                    exception is not OutOfMemoryException)
                {
                    // Dziennik nigdy nie moze kosztowac pomiaru.
                    return;
                }
            }
        }
    }

    private static void AppendShared(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            _ = Directory.CreateDirectory(directory);
        }

        using FileStream stream = new(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > SizeLimitBytes)
        {
            // Ostatnie sesje sa tymi, o ktore chodzi w zgloszeniu.
            stream.SetLength(0);
        }

        _ = stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
    }
}

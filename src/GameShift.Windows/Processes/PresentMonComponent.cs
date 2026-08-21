using System.Security.Cryptography;

namespace GameShift.Windows.Processes;

public static class PresentMonComponent
{
    public const string Version = "2.5.1";
    public const string ExecutableFileName = "PresentMon-2.5.1-x64.exe";
    public const long ExpectedFileLength = 956_768;
    public const string ExpectedSha256 =
        "9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191";

    public static string ResolveDefaultExecutablePath(
        string? applicationDirectory = null)
    {
        string baseDirectory = Path.GetFullPath(
            applicationDirectory ?? AppContext.BaseDirectory);
        string componentDirectory = Path.GetFullPath(
            Path.Combine(baseDirectory, "Tools", "PresentMon"));
        string executablePath = Path.GetFullPath(
            Path.Combine(componentDirectory, ExecutableFileName));
        string expectedPrefix = componentDirectory.TrimEnd(
            Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!executablePath.StartsWith(
                expectedPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ścieżka PresentMon wykracza poza katalog komponentu.");
        }

        return executablePath;
    }

    public static async ValueTask<PresentMonComponentInspection> InspectAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            return new(
                PresentMonComponentState.Missing,
                fullPath,
                ActualSha256: null,
                $"Brak składnika pomiarowego PresentMon {Version}.");
        }

        try
        {
            FileInfo file = new(fullPath);
            if (file.Length != ExpectedFileLength)
            {
                return new(
                    PresentMonComponentState.Invalid,
                    fullPath,
                    ActualSha256: null,
                    "Plik PresentMon ma nieoczekiwany rozmiar i nie zostanie "
                    + "uruchomiony.");
            }

            await using FileStream stream = new(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous
                    | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false);
            string actualSha256 = Convert.ToHexString(hash);
            if (!StringComparer.Ordinal.Equals(
                    actualSha256,
                    ExpectedSha256))
            {
                return new(
                    PresentMonComponentState.Invalid,
                    fullPath,
                    actualSha256,
                    "Skrót SHA-256 PresentMon nie zgadza się z przypiętym "
                    + "wydaniem open source. Pomiar został zablokowany.");
            }

            return new(
                PresentMonComponentState.Ready,
                fullPath,
                actualSha256,
                $"PresentMon {Version} jest gotowy.");
        }
        catch (UnauthorizedAccessException)
        {
            return new(
                PresentMonComponentState.AccessDenied,
                fullPath,
                ActualSha256: null,
                "Windows odmówił odczytu pliku PresentMon.");
        }
        catch (IOException exception)
        {
            return new(
                PresentMonComponentState.Invalid,
                fullPath,
                ActualSha256: null,
                $"Nie można zweryfikować PresentMon: {exception.Message}");
        }
    }
}

public enum PresentMonComponentState
{
    Missing = 1,
    Invalid = 2,
    AccessDenied = 3,
    Ready = 4,
}

public sealed record PresentMonComponentInspection(
    PresentMonComponentState State,
    string ExecutablePath,
    string? ActualSha256,
    string Message)
{
    public bool IsReady => State == PresentMonComponentState.Ready;
}

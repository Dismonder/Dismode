using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GameShift.Windows.OptiScaler;

public sealed record CompanionDownloadResult(
    bool Succeeded,
    string? FilePath,
    string Message);

/// <summary>
/// Fetches REFramework, the mod loader RE Engine games need before OptiScaler
/// will run in them.
/// <para>
/// It is praydog's open-source project under the MIT licence — a mod loader and
/// scripting platform, not a protection bypass. The stable releases ship a
/// separate build per game and none of them covers RESIDENT EVIL Requiem, so
/// the pinned build here is a CI mirror nightly, which is a single universal
/// dinput8.dll. That pin is deliberate: an unpinned "latest nightly" would mean
/// a different binary every day with nothing to check it against.
/// </para>
/// </summary>
public sealed class REFrameworkCompanionSource
{
    /// <summary>
    /// Pinned nightly. Bump this together with <see cref="PinnedSha256"/> and
    /// <see cref="PinnedRevision"/>; the three are verified as a set.
    /// </summary>
    private const string PinnedTag =
        "nightly-01417-b6baf6b406efc65e077b99cb4d9ad25b0a0a9095";

    private const string PinnedSha256 =
        "ae8208c299422ae88ec520082ac1721fc7c06f41ef163ca1eeef10650fee0e7c";

    public const string PinnedRevision =
        "b6baf6b406efc65e077b99cb4d9ad25b0a0a9095";

    private const long MaximumArchiveBytes = 64L * 1024 * 1024;
    private const long MaximumEntryBytes = 64L * 1024 * 1024;

    private static readonly Uri ArchiveUri = new(
        "https://github.com/praydog/REFramework-nightly/releases/download/"
        + PinnedTag
        + "/REFramework.zip");

    private static readonly HttpClient SharedClient = CreateClient();

    private readonly HttpClient _client;

    public REFrameworkCompanionSource(HttpClient? client = null)
    {
        _client = client ?? SharedClient;
    }

    /// <summary>
    /// Downloads the pinned archive and writes its dinput8.dll into
    /// <paramref name="destinationDirectory"/>. The caller is expected to hand
    /// that file to the normal install payload, so the same backup, manifest
    /// and uninstall bookkeeping covers it as everything else.
    /// </summary>
    public async ValueTask<CompanionDownloadResult> DownloadAsync(
        string destinationDirectory,
        string companionFileName,
        CancellationToken cancellationToken) =>
        await DownloadAsync(
                destinationDirectory,
                companionFileName,
                companionFileName,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// REFramework ships its loader as dinput8.dll, but the RE Engine layout
    /// that actually works installs it as ReShade64.dll so OptiScaler
    /// chainloads it instead of Windows loading both in parallel. So the name
    /// inside the archive and the name on disk are two different things.
    /// </summary>
    public async ValueTask<CompanionDownloadResult> DownloadAsync(
        string destinationDirectory,
        string archiveEntryName,
        string companionFileName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveEntryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(companionFileName);

        string archivePath = Path.Combine(
            destinationDirectory,
            "REFramework.zip");
        try
        {
            Directory.CreateDirectory(destinationDirectory);

            using (HttpResponseMessage response = await _client
                .GetAsync(
                    ArchiveUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new(
                        false,
                        null,
                        "Nie udało się pobrać REFramework "
                            + $"(HTTP {(int)response.StatusCode}).");
                }

                if (response.Content.Headers.ContentLength
                    is long declared and > MaximumArchiveBytes)
                {
                    return new(
                        false,
                        null,
                        $"Archiwum REFramework jest nieoczekiwanie duże "
                            + $"({declared} B).");
                }

                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using FileStream target = File.Create(archivePath);
                await CopyBoundedAsync(
                        source,
                        target,
                        MaximumArchiveBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string actual = ComputeSha256(archivePath);
            if (!actual.Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    false,
                    null,
                    "Suma kontrolna pobranego REFramework nie zgadza się "
                        + "z przypiętą. Plik nie zostanie użyty.");
            }

            string companionPath = Path.Combine(
                destinationDirectory,
                companionFileName);
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.FullName,
                        archiveEntryName,
                        StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    return new(
                        false,
                        null,
                        $"Archiwum REFramework nie zawiera {archiveEntryName}.");
                }

                if (entry.Length > MaximumEntryBytes)
                {
                    return new(
                        false,
                        null,
                        $"{archiveEntryName} w archiwum jest nieoczekiwanie "
                            + "duży.");
                }

                entry.ExtractToFile(companionPath, overwrite: true);
            }

            return new(
                true,
                companionPath,
                $"REFramework {PinnedRevision[..7]} pobrany i zweryfikowany.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or InvalidDataException
                or UnauthorizedAccessException)
        {
            return new(
                false,
                null,
                $"Nie udało się przygotować REFramework: {exception.Message}");
        }
        finally
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async ValueTask CopyBoundedAsync(
        Stream source,
        Stream target,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new InvalidDataException(
                    "Archiwum REFramework przekroczyło dopuszczalny rozmiar.");
            }

            await target
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new();
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("GameShift", "0.4"));
        return client;
    }
}

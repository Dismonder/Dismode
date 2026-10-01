using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Dismode.Core.OptiScaler;

namespace Dismode.Windows.OptiScaler;

internal sealed class GitHubOptiScalerPackageSource : IOptiScalerPackageSource
{
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 128L * 1024 * 1024;
    private const int MaximumRedirects = 5;

    // The Neural Rendering fork is pinned: a single reviewed build, verified
    // by hash. GitHub's own digest is not enough, because the author can
    // replace an asset in place and the digest would follow it.
    private const string NeuralRenderingPinnedVersion = "0.2.0-dlssnr";
    private const string NeuralRenderingPinnedSha256 =
        "8eece7a4d7de6de5917f0c99ac60540b2d77022e7699bba717b0a6d9e1829bce";
    private static readonly Uri LatestStableReleaseUri = new(
        "https://api.github.com/repos/optiscaler/OptiScaler/releases/latest");
    private static readonly HashSet<string> DownloadHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client;

    public GitHubOptiScalerPackageSource()
    {
        _client = SharedClient;
    }

    internal GitHubOptiScalerPackageSource(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = CreateClient(handler);
    }

    public async ValueTask<OptiScalerPackageDescriptor> GetLatestAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetLatestStableCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException
            or InvalidOperationException
            or FormatException
            or OverflowException)
        {
            throw new InvalidDataException(
                "Metadane wydania OptiScaler mają nieprawidłowy format.",
                exception);
        }
    }

    public async ValueTask<IReadOnlyList<OptiScalerPackageDescriptor>> GetAvailableAsync(
        OptiScalerReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
        }

        Uri releasesUri = GetReleasesUri(channel);
        byte[] metadata = await DownloadMetadataAsync(
                releasesUri,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(metadata);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Lista wydań OptiScaler ma nieprawidłowy format.");
            }

            List<OptiScalerPackageDescriptor> releases = [];
            foreach (JsonElement release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out JsonElement draft)
                    && draft.GetBoolean())
                {
                    continue;
                }

                if (channel == OptiScalerReleaseChannel.Stable
                    && release.TryGetProperty("prerelease", out JsonElement prerelease)
                    && prerelease.GetBoolean())
                {
                    continue;
                }

                try
                {
                    releases.Add(ParseRelease(release, channel));
                }
                catch (Exception exception) when (
                    exception is InvalidDataException
                    or KeyNotFoundException
                    or InvalidOperationException
                    or FormatException
                    or OverflowException)
                {
                }
            }

            if (releases.Count == 0)
            {
                throw new InvalidDataException(
                    "Wybrany kanał nie zawiera zweryfikowanego pakietu OptiScaler.");
            }

            return releases;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Metadane wydań OptiScaler mają nieprawidłowy format.",
                exception);
        }
    }

    private async ValueTask<OptiScalerPackageDescriptor> GetLatestStableCoreAsync(
        CancellationToken cancellationToken)
    {
        byte[] metadata = await DownloadMetadataAsync(
                LatestStableReleaseUri,
                cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(metadata);
        return ParseRelease(
            document.RootElement,
            OptiScalerReleaseChannel.Stable);
    }

    private async ValueTask<byte[]> DownloadMetadataAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        using HttpResponseMessage response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes)
        {
            throw new InvalidDataException("Odpowiedź GitHub jest zbyt duża.");
        }

        await using Stream stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadBoundedAsync(
                stream,
                MaximumMetadataBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static OptiScalerPackageDescriptor ParseRelease(
        JsonElement root,
        OptiScalerReleaseChannel channel)
    {
        if (root.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean()
            || channel == OptiScalerReleaseChannel.Stable
            && root.TryGetProperty("prerelease", out JsonElement prerelease)
            && prerelease.GetBoolean())
        {
            throw new InvalidDataException("Wydanie nie należy do wybranego kanału.");
        }

        string tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("Wydanie nie zawiera numeru wersji.");
        string version = tag.TrimStart('v', 'V');
        if (!IsSafeReleaseIdentifier(version))
        {
            throw new InvalidDataException("Wydanie ma nieprawidłowy numer wersji.");
        }

        JsonElement[] assets = root.GetProperty("assets")
            .EnumerateArray()
            .Where(asset =>
            {
                string? name = asset.GetProperty("name").GetString();
                return name is not null && IsPackageAssetName(name, channel);
            })
            .ToArray();
        if (assets.Length != 1)
        {
            throw new InvalidDataException(
                "Wydanie musi zawierać dokładnie jeden oficjalny pakiet.");
        }

        JsonElement asset = assets[0];
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumPackageBytes)
        {
            throw new InvalidDataException("Pakiet przekracza bezpieczny limit rozmiaru.");
        }

        string digest = asset.GetProperty("digest").GetString() ?? string.Empty;
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || digest.Length != 71
            || !digest.AsSpan(7).ContainsOnlyHexDigits())
        {
            throw new InvalidDataException("GitHub nie podał poprawnej sumy SHA-256 pakietu.");
        }

        string downloadUrl = asset.GetProperty("browser_download_url").GetString()
            ?? throw new InvalidDataException("Wydanie nie zawiera adresu pobierania.");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? downloadUri)
            || !IsReleaseAssetUri(downloadUri, channel))
        {
            throw new InvalidDataException(
                "Adres pakietu nie prowadzi do przypiętego kanału OptiScaler.");
        }

        DateTimeOffset? publishedAtUtc = null;
        if (root.TryGetProperty("published_at", out JsonElement publishedAt)
            && publishedAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(publishedAt.GetString(), out DateTimeOffset parsed))
        {
            publishedAtUtc = parsed.ToUniversalTime();
        }

        string sha256 = digest[7..].ToLowerInvariant();
        if (channel == OptiScalerReleaseChannel.DlssNeuralRendering
            && (!version.Equals(
                    NeuralRenderingPinnedVersion,
                    StringComparison.OrdinalIgnoreCase)
                || !sha256.Equals(
                    NeuralRenderingPinnedSha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Wydanie Neural Rendering nie odpowiada przypiętej, "
                    + "zweryfikowanej paczce.");
        }

        return new(
            version,
            downloadUri,
            size,
            sha256,
            channel,
            publishedAtUtc);
    }

    public async ValueTask<string> DownloadAsync(
        OptiScalerPackageDescriptor package,
        string cacheDirectory,
        CancellationToken cancellationToken) =>
        await DownloadAsync(package, cacheDirectory, null, cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<string> DownloadAsync(
        OptiScalerPackageDescriptor package,
        string cacheDirectory,
        IProgress<OptiScalerDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (!IsReleaseAssetUri(package.DownloadUri, package.Channel))
        {
            throw new InvalidDataException(
                "Pakiet nie należy do wybranego kanału OptiScaler.");
        }

        Directory.CreateDirectory(cacheDirectory);
        string destination = Path.Combine(
            cacheDirectory,
            $"OptiScaler-{package.Channel}-{package.Version}-"
                + $"{package.Sha256[..12]}{GetArchiveExtension(package.Channel)}");
        if (File.Exists(destination)
            && new FileInfo(destination).Length == package.Size
            && ComputeSha256(destination).Equals(
                package.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Uri current = package.DownloadUri;
            for (int redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                ValidateDownloadUri(current);
                using HttpRequestMessage request = new(HttpMethod.Get, current);
                using HttpResponseMessage response = await _client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == MaximumRedirects || response.Headers.Location is null)
                    {
                        throw new InvalidDataException("GitHub zwrócił nieprawidłowe przekierowanie.");
                    }

                    current = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(current, response.Headers.Location);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength
                    && (contentLength != package.Size
                        || contentLength > MaximumPackageBytes))
                {
                    throw new InvalidDataException("Rozmiar pobieranego pakietu jest niezgodny.");
                }

                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                long total;
                string actualHash;
                await using (FileStream destinationStream = new(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    using IncrementalHash hash = IncrementalHash.CreateHash(
                        HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[81920];
                    total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaximumPackageBytes || total > package.Size)
                        {
                            throw new InvalidDataException(
                                "Pobierany pakiet przekroczył limit rozmiaru.");
                        }

                        hash.AppendData(buffer, 0, read);
                        await destinationStream.WriteAsync(
                                buffer.AsMemory(0, read),
                                cancellationToken)
                            .ConfigureAwait(false);
                        progress?.Report(new(total, package.Size));
                    }

                    await destinationStream.FlushAsync(cancellationToken)
                        .ConfigureAwait(false);
                    actualHash = Convert.ToHexString(hash.GetHashAndReset())
                        .ToLowerInvariant();
                }

                if (total != package.Size
                    || !actualHash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Suma SHA-256 pobranego pakietu jest niezgodna.");
                }

                byte[] expected = GetArchiveSignature(package.Channel);
                byte[] signature = new byte[expected.Length];
                await using (FileStream verifyStream = File.OpenRead(temporary))
                {
                    int signatureBytes = await verifyStream.ReadAsync(
                            signature,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (signatureBytes != signature.Length
                        || !signature.AsSpan().SequenceEqual(expected))
                    {
                        throw new InvalidDataException(
                            "Pobrany plik nie jest archiwum "
                                + $"{GetArchiveExtension(package.Channel)[1..]}.");
                    }
                }

                File.Move(temporary, destination, overwrite: true);
                return destination;
            }

            throw new InvalidDataException("Przekroczono limit przekierowań GitHub.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        HttpClient client = handler is null
            ? new(new HttpClientHandler { AllowAutoRedirect = false })
            : new(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(45);
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Dismode", "0.4"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using MemoryStream destination = new();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > maximumBytes)
            {
                throw new InvalidDataException("Odpowiedź przekroczyła limit rozmiaru.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || !DownloadHosts.Contains(uri.Host)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("Przekierowanie prowadzi poza dozwolone serwery GitHub.");
        }
    }

    private static Uri GetReleasesUri(OptiScalerReleaseChannel channel) =>
        channel switch
        {
            OptiScalerReleaseChannel.Stable => new(
                "https://api.github.com/repos/optiscaler/OptiScaler/releases?per_page=10"),
            OptiScalerReleaseChannel.Beta => new(
                "https://api.github.com/repos/Optiscaler-Client/Optiscaler-Betas/releases?per_page=10"),
            OptiScalerReleaseChannel.Nightly => new(
                "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases?per_page=10"),
            OptiScalerReleaseChannel.DlssNeuralRendering => new(
                "https://api.github.com/repos/Dagherbou/OptiScaler_DLSSNR/releases?per_page=10"),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
        };

    private static bool IsReleaseAssetUri(
        Uri uri,
        OptiScalerReleaseChannel channel)
    {
        string expectedPath = channel switch
        {
            OptiScalerReleaseChannel.Stable =>
                "/optiscaler/OptiScaler/releases/download/",
            OptiScalerReleaseChannel.Beta =>
                "/Optiscaler-Client/Optiscaler-Betas/releases/download/",
            OptiScalerReleaseChannel.Nightly =>
                "/optiscaler/OptiScaler-nightly/releases/download/",
            OptiScalerReleaseChannel.DlssNeuralRendering =>
                "/Dagherbou/OptiScaler_DLSSNR/releases/download/",
            _ => string.Empty,
        };
        return expectedPath.Length > 0
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.AbsolutePath.StartsWith(
                expectedPath,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Archive format published by a channel. The Neural Rendering fork ships
    /// a zip; the official channels ship 7z. The magic bytes and the cache
    /// file extension must both follow the channel, otherwise a valid package
    /// is rejected as corrupt.
    /// </summary>
    private static byte[] GetArchiveSignature(
        OptiScalerReleaseChannel channel) =>
        channel == OptiScalerReleaseChannel.DlssNeuralRendering
            ? [0x50, 0x4B, 0x03, 0x04]
            : [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    private static string GetArchiveExtension(
        OptiScalerReleaseChannel channel) =>
        channel == OptiScalerReleaseChannel.DlssNeuralRendering
            ? ".zip"
            : ".7z";

    private static bool IsPackageAssetName(
        string name,
        OptiScalerReleaseChannel channel) =>
        channel == OptiScalerReleaseChannel.DlssNeuralRendering
            ? name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            : name.StartsWith("Optiscaler_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeReleaseIdentifier(string value) =>
        value.Length is > 0 and <= 64
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_');

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

file static class HexSpanExtensions
{
    public static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}

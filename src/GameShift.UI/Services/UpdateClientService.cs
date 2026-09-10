using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GameShift.Core.Product;
using GameShift.Core.Updates;

namespace GameShift.UI.Services;

public sealed record UpdateCheckResult(
    UpdateCheckState State,
    string Message,
    SignedUpdateManifest? Manifest,
    UpdatePreferences Preferences);

public sealed record UpdateDownloadProgress(
    int CompletedChunks,
    int TotalChunks,
    long DownloadedBytes,
    long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0d
        : Math.Clamp(
            DownloadedBytes * 100d / TotalBytes,
            0d,
            100d);
}

public sealed class UpdateClientService : IDisposable
{
    private const int MaximumManifestBytes = 128 * 1024;
    private static readonly TimeSpan ManifestTimeout =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChunkTimeout =
        TimeSpan.FromMinutes(5);

    private readonly IUpdatePreferencesRepository _preferencesRepository;
    private readonly string _stagingRoot;
    private readonly Uri _serviceBaseUri;
    private readonly byte[] _trustedPublicKey;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public UpdateClientService(
        IUpdatePreferencesRepository preferencesRepository,
        string stagingRoot,
        HttpMessageHandler? handler = null)
    {
        _preferencesRepository = preferencesRepository
            ?? throw new ArgumentNullException(nameof(preferencesRepository));
        _stagingRoot = Path.GetFullPath(stagingRoot);
        _serviceBaseUri = new(
            ProductInformation.UpdateServiceBaseUri,
            UriKind.Absolute);
        _trustedPublicKey = Convert.FromBase64String(
            ProductInformation
                .TrustedUpdatePublicKeySubjectPublicKeyInfoBase64);
        _httpClient = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async ValueTask<UpdateCheckResult> CheckAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UpdatePreferences preferences =
            await _preferencesRepository.LoadUpdatePreferencesAsync(
                cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!manual && !preferences.IsAutomaticCheckDue(now))
        {
            return new(
                UpdateCheckState.NotDue,
                "Automatyczne sprawdzenie nie jest jeszcze potrzebne.",
                Manifest: null,
                preferences);
        }

        try
        {
            SignedUpdateManifest manifest = await DownloadManifestAsync(
                preferences.Channel,
                cancellationToken);
            UpdatePreferences saved = preferences.WithSuccessfulCheck(
                now,
                manifest.Version);
            await _preferencesRepository.SaveUpdatePreferencesAsync(
                saved,
                cancellationToken);

            UpdateVersionDecision decision = UpdateVersionPolicy.Classify(
                ProductInformation.CurrentVersion,
                manifest);
            return new(
                decision.State,
                decision.Message,
                decision.Manifest,
                saved);
        }
        catch (OperationCanceledException exception) when (
            UpdateFailurePolicy.IsUserCancellation(
                exception,
                cancellationToken))
        {
            throw;
        }
        catch (Exception exception) when (
            UpdateFailurePolicy.IsHandled(exception))
        {
            string message = UpdateFailurePolicy.Describe(exception);
            UpdatePreferences failed = preferences.WithError(now, message);
            await _preferencesRepository.SaveUpdatePreferencesAsync(
                failed,
                CancellationToken.None);
            return new(
                UpdateCheckState.Failed,
                message,
                Manifest: null,
                failed);
        }
    }

    public async ValueTask<string> DownloadInstallerAsync(
        SignedUpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);

        SignedUpdateManifest verified =
            UpdateManifestCodec.DeserializeAndVerify(
                UpdateManifestCodec.Serialize(manifest),
                _serviceBaseUri,
                manifest.Channel,
                ProductInformation.TrustedUpdateKeyId,
                _trustedPublicKey);
        string versionDirectory = EnsureStagingChild(verified.Version);
        Directory.CreateDirectory(versionDirectory);

        long downloadedBytes = 0;
        for (int index = 0; index < verified.Installer.Chunks.Count; index++)
        {
            UpdatePackageChunk chunk = verified.Installer.Chunks[index];
            string chunkFileName = Path.GetFileName(chunk.Path);
            string chunkPath = EnsureChildPath(
                versionDirectory,
                chunkFileName);
            if (!await FileMatchesAsync(
                    chunkPath,
                    chunk.SizeBytes,
                    chunk.Sha256,
                    cancellationToken))
            {
                await DownloadChunkAsync(
                    chunk,
                    chunkPath,
                    cancellationToken);
            }

            downloadedBytes += chunk.SizeBytes;
            progress?.Report(new(
                CompletedChunks: index + 1,
                TotalChunks: verified.Installer.Chunks.Count,
                DownloadedBytes: downloadedBytes,
                TotalBytes: verified.Installer.SizeBytes));
        }

        string installerPath = EnsureChildPath(
            versionDirectory,
            verified.Installer.FileName);
        if (await FileMatchesAsync(
                installerPath,
                verified.Installer.SizeBytes,
                verified.Installer.Sha256,
                cancellationToken)
            && await HasPortableExecutableHeaderAsync(
                installerPath,
                cancellationToken))
        {
            return installerPath;
        }

        string temporaryInstallerPath = installerPath + ".assembling";
        try
        {
            await AssembleInstallerAsync(
                verified,
                versionDirectory,
                temporaryInstallerPath,
                cancellationToken);
            File.Move(
                temporaryInstallerPath,
                installerPath,
                overwrite: true);
            return installerPath;
        }
        finally
        {
            TryDeleteFile(temporaryInstallerPath);
        }
    }

    /// <summary>
    /// Verifies the downloaded installer and returns the handle it was
    /// verified through. The caller must keep that handle open until the
    /// installer has been started.
    /// <para>
    /// Checking the hash and then closing the file leaves a window in which
    /// anything running as the user can replace it — and the launch that
    /// follows asks for elevation. The user approves that prompt believing it
    /// is GameShift's update, so the swap turns into a privilege escalation.
    /// The handle denies writes and deletes for as long as it is held, which
    /// closes the window rather than narrowing it.
    /// </para>
    /// </summary>
    public async ValueTask<FileStream> VerifyInstallerAsync(
        SignedUpdateManifest manifest,
        string installerPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        SignedUpdateManifest verified =
            UpdateManifestCodec.DeserializeAndVerify(
                UpdateManifestCodec.Serialize(manifest),
                _serviceBaseUri,
                manifest.Channel,
                ProductInformation.TrustedUpdateKeyId,
                _trustedPublicKey);
        string versionDirectory = EnsureStagingChild(verified.Version);
        string expectedPath = EnsureChildPath(
            versionDirectory,
            verified.Installer.FileName);
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(installerPath),
                expectedPath))
        {
            throw new InvalidDataException(
                "Instalator nie pochodzi z chronionego katalogu stagingu.");
        }

        // Uchwyt otwarty raz i trzymany: wszystkie sprawdzenia biegna przez
        // niego, wiec dotycza tych samych bajtow, ktore potem sie uruchomia.
        FileStream guard = new(
            expectedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (guard.Length != verified.Installer.SizeBytes
                || !await StreamMatchesHashAsync(
                    guard,
                    verified.Installer.Sha256,
                    cancellationToken)
                || !await HasPortableExecutableHeaderAsync(
                    guard,
                    cancellationToken))
            {
                throw new CryptographicException(
                    "Instalator nie jest zgodny z podpisanym manifestem.");
            }
        }
        catch
        {
            await guard.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return guard;
    }

    private async ValueTask<SignedUpdateManifest> DownloadManifestAsync(
        string channel,
        CancellationToken cancellationToken)
    {
        Uri manifestUri = new(
            _serviceBaseUri,
            $"/v1/channels/{channel}/manifest.json");
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            manifestUri);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(ManifestTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSuccessfulDirectResponse(response, manifestUri);
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "Manifest aktualizacji przekracza limit rozmiaru.");
        }

        await using Stream stream =
            await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] json = await ReadLimitedAsync(
            stream,
            MaximumManifestBytes,
            timeout.Token);
        return UpdateManifestCodec.DeserializeAndVerify(
            json,
            _serviceBaseUri,
            channel,
            ProductInformation.TrustedUpdateKeyId,
            _trustedPublicKey);
    }

    private async ValueTask DownloadChunkAsync(
        UpdatePackageChunk chunk,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Uri chunkUri = new(_serviceBaseUri, chunk.Path);
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            chunkUri);
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeout.CancelAfter(ChunkTimeout);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        EnsureSuccessfulDirectResponse(response, chunkUri);
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength != chunk.SizeBytes)
        {
            throw new InvalidDataException(
                "Fragment aktualizacji ma inny rozmiar niż podpisany manifest.");
        }

        string temporaryPath = targetPath + ".download";
        TryDeleteFile(temporaryPath);
        try
        {
            await using Stream input =
                await response.Content.ReadAsStreamAsync(timeout.Token);
            await using FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1024 * 1024];
            long totalBytes = 0;
            while (true)
            {
                int read = await input.ReadAsync(
                    buffer,
                    timeout.Token);
                if (read == 0)
                {
                    break;
                }

                checked
                {
                    totalBytes += read;
                }

                if (totalBytes > chunk.SizeBytes)
                {
                    throw new InvalidDataException(
                        "Fragment aktualizacji przekroczył podpisany rozmiar.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    timeout.Token);
            }

            await output.FlushAsync(timeout.Token);
            output.Flush(flushToDisk: true);
            string actualHash = Convert.ToHexString(
                hash.GetHashAndReset());
            if (totalBytes != chunk.SizeBytes
                || !string.Equals(
                    actualHash,
                    chunk.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CryptographicException(
                    "Fragment aktualizacji nie przeszedł weryfikacji SHA-256.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async ValueTask AssembleInstallerAsync(
        SignedUpdateManifest manifest,
        string versionDirectory,
        string targetPath,
        CancellationToken cancellationToken)
    {
        TryDeleteFile(targetPath);
        await using FileStream output = new(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long totalBytes = 0;
        foreach (UpdatePackageChunk chunk in manifest.Installer.Chunks)
        {
            string path = EnsureChildPath(
                versionDirectory,
                Path.GetFileName(chunk.Path));
            await using FileStream input = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await input.ReadAsync(
                    buffer,
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                checked
                {
                    totalBytes += read;
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        string actualHash = Convert.ToHexString(hash.GetHashAndReset());
        if (totalBytes != manifest.Installer.SizeBytes
            || !string.Equals(
                actualHash,
                manifest.Installer.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                "Złożony instalator nie przeszedł weryfikacji SHA-256.");
        }

        if (!await HasPortableExecutableHeaderAsync(
                targetPath,
                cancellationToken))
        {
            throw new InvalidDataException(
                "Złożony instalator nie ma nagłówka Windows PE.");
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            ProductInformation.DisplayName,
            ProductInformation.CurrentVersion));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureSuccessfulDirectResponse(
        HttpResponseMessage response,
        Uri expectedUri)
    {
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException(
                "Serwer aktualizacji próbował przekierować żądanie.");
        }

        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not Uri actualUri
            || !string.Equals(
                actualUri.Scheme,
                expectedUri.Scheme,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                actualUri.Host,
                expectedUri.Host,
                StringComparison.OrdinalIgnoreCase)
            || actualUri.Port != expectedUri.Port)
        {
            throw new HttpRequestException(
                "Odpowiedź aktualizacji pochodzi z niezaufanego serwera.");
        }
    }

    private static async ValueTask<byte[]> ReadLimitedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    "Manifest aktualizacji przekracza limit rozmiaru.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async ValueTask<bool> FileMatchesAsync(
        string path,
        long expectedSize,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize)
        {
            return false;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(
            actualHash,
            expectedHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<bool> StreamMatchesHashAsync(
        FileStream stream,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        string actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(
            actualHash,
            expectedHash,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wersja dla sciezek, ktore nie koncza sie uruchomieniem pliku —
    /// sprawdzenia w trakcie skladania paczki. Tam podmiana miedzy odczytem
    /// a uzyciem nie daje nikomu nic, bo nic sie jeszcze nie wykonuje.
    /// </summary>
    private static async ValueTask<bool> HasPortableExecutableHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 2,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HasPortableExecutableHeaderAsync(
            stream,
            cancellationToken);
    }

    private static async ValueTask<bool> HasPortableExecutableHeaderAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        byte[] header = new byte[2];
        int read = await stream.ReadAsync(header, cancellationToken);
        return read == 2 && header[0] == 0x4D && header[1] == 0x5A;
    }

    private string EnsureStagingChild(string childName) =>
        EnsureChildPath(_stagingRoot, childName);

    private static string EnsureChildPath(
        string parent,
        string childName)
    {
        string fullParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(
            Path.Combine(fullParent, childName));
        if (!candidate.StartsWith(
                fullParent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Ścieżka aktualizacji opuściła katalog stagingu.");
        }

        return candidate;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}

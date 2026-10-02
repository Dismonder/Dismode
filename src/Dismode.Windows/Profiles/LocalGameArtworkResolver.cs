using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Dismode.Windows.Profiles;

public sealed class LocalGameArtworkResolver
{
    private const long MaximumArtworkBytes = 32L * 1024 * 1024;
    private const long MaximumEpicCatalogBytes = 16L * 1024 * 1024;
    private const int MaximumEpicCatalogItems = 4096;
    private const int MaximumCandidateFiles = 256;
    private const int MaximumDirectoryDepth = 2;
    private readonly string[] _steamRoots;
    private readonly string[] _epicCatalogPaths;
    private readonly string[] _epicManifestPaths;
    private readonly string _thumbnailCacheDirectory;
    private readonly IExecutableArtworkExtractor _executableExtractor;
    private readonly HttpClient _artworkHttpClient;
    private readonly TimeSpan _networkTimeout;
    private readonly IArtworkResolutionObserver? _resolutionObserver;

    public LocalGameArtworkResolver(
        IEnumerable<string>? steamRoots = null,
        string? thumbnailCacheDirectory = null,
        HttpMessageHandler? artworkHttpHandler = null)
        : this(
            steamRoots,
            thumbnailCacheDirectory,
            new WindowsExecutableArtworkExtractor(),
            artworkHttpHandler)
    {
    }

    internal LocalGameArtworkResolver(
        IEnumerable<string>? steamRoots,
        string? thumbnailCacheDirectory,
        IExecutableArtworkExtractor executableExtractor,
        HttpMessageHandler? artworkHttpHandler = null,
        TimeSpan? networkTimeout = null,
        IArtworkResolutionObserver? resolutionObserver = null,
        IEnumerable<string>? epicCatalogPaths = null,
        IEnumerable<string>? epicManifestPaths = null)
    {
        ArgumentNullException.ThrowIfNull(executableExtractor);
        _steamRoots = (steamRoots ?? FindDefaultSteamRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _epicCatalogPaths = (epicCatalogPaths ?? FindDefaultEpicCatalogPaths())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(Path.IsPathFullyQualified)
            .Where(path => !path.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _thumbnailCacheDirectory = Path.GetFullPath(
            thumbnailCacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Dismode",
                "Artwork"));
        _epicManifestPaths = (epicManifestPaths ?? FindDefaultEpicManifestPaths())
            .Where(Path.IsPathFullyQualified)
            .Where(path => !path.StartsWith(@"\\", StringComparison.Ordinal))
            .Take(256)
            .ToArray();
        _executableExtractor = executableExtractor;
        _artworkHttpClient = artworkHttpHandler is null
            ? DefaultArtworkHttpClient
            : new HttpClient(artworkHttpHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        _networkTimeout = networkTimeout ?? TimeSpan.FromSeconds(6);
        _resolutionObserver = resolutionObserver;
    }

    public async ValueTask<GameArtwork?> ResolveAsync(
        DetectedGame game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        string executablePath = Path.GetFullPath(game.ExecutablePath);
        if (!IsExecutablePath(executablePath))
        {
            return null;
        }

        return await ResolveRoleCoreAsync(
                game,
                executablePath,
                GameArtworkRole.Poster,
                new ArtworkResolutionBudget(
                    GameArtworkRole.Poster,
                    _resolutionObserver),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GameArtworkSet> ResolveSetAsync(
        DetectedGame game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        string executablePath = Path.GetFullPath(game.ExecutablePath);
        if (!IsExecutablePath(executablePath))
        {
            return new(null, null);
        }

        GameArtwork? poster = await ResolveRoleCoreAsync(
                game,
                executablePath,
                GameArtworkRole.Poster,
                new ArtworkResolutionBudget(
                    GameArtworkRole.Poster,
                    _resolutionObserver),
                cancellationToken)
            .ConfigureAwait(false);
        GameArtwork? hero = await ResolveRoleCoreAsync(
                game,
                executablePath,
                GameArtworkRole.Hero,
                new ArtworkResolutionBudget(
                    GameArtworkRole.Hero,
                    _resolutionObserver),
                cancellationToken)
            .ConfigureAwait(false);
        return new(poster, hero);
    }

    internal async ValueTask<GameArtwork?> ResolveRoleAsync(
        DetectedGame game,
        GameArtworkRole role,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        string executablePath = Path.GetFullPath(game.ExecutablePath);
        if (!IsExecutablePath(executablePath))
        {
            return null;
        }

        return await ResolveRoleCoreAsync(
                game,
                executablePath,
                role,
                new ArtworkResolutionBudget(role, _resolutionObserver),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsExecutablePath(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    internal bool IsArtworkValidForRole(
        string? path,
        GameArtworkRole role,
        CancellationToken cancellationToken) =>
        !HasDiscouragedArtworkName(path)
        && IsSafeArtworkFileForRole(
            path,
            role,
            new ArtworkResolutionBudget(role, _resolutionObserver),
            cancellationToken);

    private async ValueTask<GameArtwork?> ResolveRoleCoreAsync(
        DetectedGame game,
        string executablePath,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        if (game.Source.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && IsNumericAppId(game.ExternalId))
        {
            if (FindSteamArtwork(
                    game.ExternalId,
                    role,
                    budget,
                    cancellationToken) is string localSteam)
            {
                return new(localSteam, GameArtworkSource.SteamLibraryCache);
            }

        }

        string installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException(
                "The game executable has no installation directory.");
        // A saved profile outlives its installation: a library moved to a
        // drive that is no longer attached still has a store identity, and
        // the store's artwork does not need the executable. Only the sources
        // that read the installation are skipped.
        bool installed = Directory.Exists(installDirectory);
        if (installed
            && game.Source.Equals("Xbox", StringComparison.OrdinalIgnoreCase)
            && FindXboxShellVisualsArtwork(
                    installDirectory,
                    role,
                    budget,
                    cancellationToken) is string shellVisual)
        {
            return new(shellVisual, GameArtworkSource.GameDirectory);
        }
        if (installed
            && !game.Source.Equals(
                "Roblox",
                StringComparison.OrdinalIgnoreCase)
            && FindGameDirectoryArtwork(
                    installDirectory,
                    game.DisplayName,
                    executablePath,
                    role,
                    budget,
                    cancellationToken) is string localArtwork)
        {
            return new(localArtwork, GameArtworkSource.GameDirectory);
        }

        if (IsEpicSource(game.Source))
        {
            DetectedGame epicGame = await ResolveEpicManifestIdentityAsync(game, cancellationToken)
                .ConfigureAwait(false);
            if (IsEpicCatalogId(epicGame.ExternalId))
            {
                string targetPath = GetEpicCachePath(epicGame, role);
                if (IsSafeArtworkFileForRole(targetPath, role, budget, cancellationToken, out _, allowAnyAspect: true))
                {
                    return new(targetPath, GameArtworkSource.EpicCatalogCache);
                }
                // Reuse downloads made before catalog namespaces were included in cache keys.
                string legacyPath = Path.Combine(_thumbnailCacheDirectory,
                    $"epic_{epicGame.ExternalId}_{role.ToString().ToLowerInvariant()}.jpg");
                if (epicGame.CatalogNamespace is not null
                    && IsSafeArtworkFileForRole(legacyPath, role, budget, cancellationToken))
                {
                    return new(legacyPath, GameArtworkSource.EpicCatalogCache);
                }

                IReadOnlyList<EpicCatalogArtwork> epicArtwork = await FindEpicCatalogArtworkAsync(
                        epicGame, role, cancellationToken).ConfigureAwait(false);
                string? downloadedEpic = await DownloadEpicArtworkAsync(
                        targetPath, epicArtwork, role, budget, cancellationToken).ConfigureAwait(false);
                if (downloadedEpic is not null)
                {
                    return new(downloadedEpic, GameArtworkSource.EpicCatalogCache);
                }
            }
        }

        if (game.Source.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && IsNumericAppId(game.ExternalId))
        {
            string? downloadedSteam = await DownloadSteamArtworkAsync(
                    game.ExternalId,
                    role,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false);
            if (downloadedSteam is not null)
            {
                return new(downloadedSteam, GameArtworkSource.SteamOfficialCdn);
            }
        }

        if (!budget.HasDecodeCapacity)
        {
            return null;
        }

        if (!File.Exists(executablePath))
        {
            return null;
        }

        string? extracted = await _executableExtractor.ExtractAsync(
                executablePath,
                _thumbnailCacheDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsSafeExecutableArtworkFallback(
                extracted,
                budget,
                cancellationToken))
        {
            return null;
        }

        return new(
            Path.GetFullPath(extracted!),
            GameArtworkSource.ExecutableThumbnail);
    }

    private static string? FindXboxShellVisualsArtwork(
        string installDirectory,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        string configPath = Path.Combine(installDirectory, "MicrosoftGame.Config");
        try
        {
            if (!budget.TryInspectName(cancellationToken))
            {
                return null;
            }

            FileInfo config = new(configPath);
            if (!config.Exists || config.Length is <= 0 or > 1024 * 1024)
            {
                return null;
            }

            XDocument document = XDocument.Load(configPath, LoadOptions.None);
            cancellationToken.ThrowIfCancellationRequested();
            XElement? shellVisualsElement = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals(
                    "ShellVisuals",
                    StringComparison.OrdinalIgnoreCase));
            string[] attributes = role is GameArtworkRole.Poster
                ? ["PosterImage", "Square300x300Logo", "Square150x150Logo", "StoreLogo"]
                : ["HeroImage", "SplashScreenImage", "Wide310x150Logo"];
            string? shellVisualsPath = attributes
                .Select(attribute => shellVisualsElement?.Attribute(attribute)?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(shellVisualsPath)
                || Path.IsPathFullyQualified(shellVisualsPath))
            {
                return null;
            }

            string installationRoot = Path.GetFullPath(installDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string visualRoot = Path.GetFullPath(Path.Combine(
                installDirectory,
                shellVisualsPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!visualRoot.StartsWith(
                    installationRoot,
                    StringComparison.OrdinalIgnoreCase)
                || (!File.Exists(visualRoot) && !Directory.Exists(visualRoot)))
            {
                return null;
            }

            if (!budget.TryInspectName(cancellationToken)
                || !TryGetSafeFileSystemEntry(
                    visualRoot,
                    out bool isDirectory))
            {
                return null;
            }

            if (!isDirectory)
            {
                return IsSafeArtworkFileForRole(
                        visualRoot,
                        role,
                        budget,
                        cancellationToken)
                    ? visualRoot
                    : null;
            }

            foreach (ArtworkEnumeratedFile file in EnumerateSafeFiles(
                         visualRoot,
                         int.MaxValue,
                         budget,
                         cancellationToken))
            {
                if (IsSafeArtworkFileForRole(
                        file.Path,
                        role,
                        budget,
                        cancellationToken))
                {
                    return file.Path;
                }
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or System.Xml.XmlException
                or ArgumentException
                or NotSupportedException)
        {
            return null;
        }
    }

    private string GetEpicCachePath(DetectedGame game, GameArtworkRole role)
    {
        string namespaceKey = game.CatalogNamespace is null
            ? string.Empty
            : "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.CatalogNamespace)).AsSpan(0, 8));
        return Path.Combine(_thumbnailCacheDirectory,
            $"epic_{game.ExternalId}{namespaceKey}_{role.ToString().ToLowerInvariant()}.jpg");
    }

    private async ValueTask<DetectedGame> ResolveEpicManifestIdentityAsync(
        DetectedGame game, CancellationToken cancellationToken)
    {
        if (game.CatalogNamespace is not null && IsEpicCatalogId(game.ExternalId))
        {
            return game;
        }

        foreach (string path in _epicManifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReadableEpicCatalogFile(path))
            {
                continue;
            }

            try
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    continue;
                }
                using JsonDocument manifest = JsonDocument.Parse(
                    await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                    new JsonDocumentOptions { MaxDepth = 32 });
                JsonElement root = manifest.RootElement;
                string? id = ReadEpicString(root, "CatalogItemId");
                if (id is null || !IsEpicCatalogId(id))
                {
                    continue;
                }
                string? appName = ReadEpicString(root, "AppName");
                string? install = ReadEpicString(root, "InstallLocation");
                string? launch = ReadEpicString(root, "LaunchExecutable");
                bool executableMatches = install is not null && launch is not null
                    && Path.IsPathFullyQualified(install)
                    && string.Equals(Path.GetFullPath(Path.Combine(install, launch)),
                        Path.GetFullPath(game.ExecutablePath), StringComparison.OrdinalIgnoreCase);
                if (string.Equals(id, game.ExternalId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(appName, game.ExternalId, StringComparison.OrdinalIgnoreCase)
                    || executableMatches)
                {
                    return game with { ExternalId = id, CatalogNamespace = ReadEpicString(root, "CatalogNamespace") };
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException)
            {
            }
        }
        return game;
    }

    private static string? ReadEpicString(JsonElement item, string property) =>
        item.ValueKind is JsonValueKind.Object
        && item.TryGetProperty(property, out JsonElement value)
        && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private async ValueTask<IReadOnlyList<EpicCatalogArtwork>> FindEpicCatalogArtworkAsync(
        DetectedGame game,
        GameArtworkRole role,
        CancellationToken cancellationToken)
    {
        foreach (string catalogPath in _epicCatalogPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsReadableEpicCatalogFile(catalogPath))
            {
                continue;
            }

            try
            {
                string encoded = await File.ReadAllTextAsync(
                        catalogPath,
                        Encoding.UTF8,
                        cancellationToken)
                    .ConfigureAwait(false);
                string trimmed = encoded.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                byte[] json = trimmed.StartsWith('[') || trimmed.StartsWith('{')
                    ? Encoding.UTF8.GetBytes(trimmed)
                    : Convert.FromBase64String(trimmed);
                using JsonDocument document = JsonDocument.Parse(
                    json,
                    new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                int inspectedItems = 0;
                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++inspectedItems > MaximumEpicCatalogItems)
                    {
                        break;
                    }

                    if (item.ValueKind is not JsonValueKind.Object
                        || !item.TryGetProperty("id", out JsonElement id)
                        || id.ValueKind is not JsonValueKind.String
                        || !string.Equals(
                            id.GetString(),
                            game.ExternalId,
                            StringComparison.OrdinalIgnoreCase)
                        || game.CatalogNamespace is not null
                            && (!item.TryGetProperty("namespace", out JsonElement catalogNamespace)
                                || catalogNamespace.ValueKind is not JsonValueKind.String
                                || !string.Equals(catalogNamespace.GetString(), game.CatalogNamespace, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    IReadOnlyList<EpicCatalogArtwork> candidates = SelectEpicCatalogArtwork(item, role);
                    if (candidates.Count > 0)
                    {
                        return candidates;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or DecoderFallbackException
                    or FormatException
                    or JsonException)
            {
            }
        }

        return [];
    }

    private static EpicCatalogArtwork[] SelectEpicCatalogArtwork(
        JsonElement item,
        GameArtworkRole role)
    {
        if (!item.TryGetProperty("keyImages", out JsonElement keyImages)
            || keyImages.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        List<EpicCatalogArtwork> candidates = [];
        int inspectedImages = 0;
        foreach (JsonElement image in keyImages.EnumerateArray())
        {
            if (++inspectedImages > 64
                || image.ValueKind is not JsonValueKind.Object
                || !image.TryGetProperty("type", out JsonElement typeElement)
                || !image.TryGetProperty("url", out JsonElement urlElement)
                || typeElement.ValueKind is not JsonValueKind.String
                || urlElement.ValueKind is not JsonValueKind.String
                || !Uri.TryCreate(
                    urlElement.GetString(),
                    UriKind.Absolute,
                    out Uri? url)
                || !IsAllowedEpicHost(url))
            {
                continue;
            }

            int score = ScoreEpicImageType(typeElement.GetString(), role);
            if (score < 0)
            {
                continue;
            }
            if (image.TryGetProperty("width", out JsonElement widthElement)
                && image.TryGetProperty("height", out JsonElement heightElement)
                && widthElement.ValueKind is JsonValueKind.Number
                && heightElement.ValueKind is JsonValueKind.Number
                && widthElement.TryGetInt32(out int width)
                && heightElement.TryGetInt32(out int height)
                && MatchesArtworkRole(width, height, role))
            {
                score += 1000;
            }
            candidates.Add(new(url, score));
        }

        return candidates.OrderByDescending(candidate => candidate.Score)
            .DistinctBy(candidate => candidate.Url).Take(8).ToArray();
    }

    private static int ScoreEpicImageType(
        string? imageType,
        GameArtworkRole role)
    {
        string type = imageType?.ToUpperInvariant() ?? string.Empty;
        return type switch
        {
            "DIESELGAMEBOXTALL" => role is GameArtworkRole.Poster ? 300 : 50,
            "OFFERIMAGETALL" => role is GameArtworkRole.Poster ? 250 : 40,
            "DIESELGAMEBOXWIDE" => role is GameArtworkRole.Hero ? 300 : 50,
            "DIESELGAMEBOX" => role is GameArtworkRole.Hero ? 280 : 60,
            "OFFERIMAGEWIDE" => role is GameArtworkRole.Hero ? 250 : 40,
            "THUMBNAIL" => 100,
            "ANDROIDICON" or "ICON" => 10,
            _ => -1,
        };
    }

    private static readonly HttpClient DefaultArtworkHttpClient = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private async ValueTask<string?> DownloadSteamArtworkAsync(
        string appId,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        if (!IsNumericAppId(appId))
        {
            return null;
        }

        string targetPath = Path.Combine(
            _thumbnailCacheDirectory,
            $"steam_{appId}_{role.ToString().ToLowerInvariant()}.jpg");

        string[] urls = role is GameArtworkRole.Poster
            ?
            [
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            ]
            :
            [
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg",
                $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            ];

        return await DownloadArtworkAsync(
                urls,
                targetPath,
                role,
                budget,
                IsAllowedSteamHost,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<string?> DownloadEpicArtworkAsync(
        string targetPath,
        IReadOnlyList<EpicCatalogArtwork> artwork,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        return await DownloadArtworkAsync(
                artwork.Select(candidate => candidate.Url.AbsoluteUri),
                targetPath,
                role,
                budget,
                IsAllowedEpicHost,
                cancellationToken,
                allowAnyAspect: true)
            .ConfigureAwait(false);
    }

    private async ValueTask<string?> DownloadArtworkAsync(
        IEnumerable<string> urls,
        string targetPath,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        Func<Uri?, bool> isAllowedHost,
        CancellationToken cancellationToken,
        bool allowAnyAspect = false)
    {
        if (budget.TryInspectName(cancellationToken)
            && IsSafeArtworkFileForRole(
                targetPath,
                role,
                budget,
                cancellationToken,
                out _,
                allowAnyAspect))
        {
            return Path.GetFullPath(targetPath);
        }

        using CancellationTokenSource timeout = new(_networkTimeout);
        using CancellationTokenSource downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            foreach (string url in urls)
            {
                string? downloaded = await DownloadArtworkUrlAsync(
                    url,
                    targetPath,
                    role,
                    budget,
                    isAllowedHost,
                    allowAnyAspect,
                    downloadCancellation.Token)
                .ConfigureAwait(false);
                if (downloaded is not null)
                {
                    return downloaded;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    private async ValueTask<string?> DownloadArtworkUrlAsync(
        string url,
        string targetPath,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        Func<Uri?, bool> isAllowedHost,
        bool allowAnyAspect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using CancellationTokenSource timeout = new(_networkTimeout);
            using CancellationTokenSource requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _artworkHttpClient
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!isAllowedHost(response.RequestMessage?.RequestUri)
                || !IsSupportedImageContentType(mediaType)
                || response.Content.Headers.ContentLength is > MaximumArtworkBytes)
            {
                return null;
            }

            Directory.CreateDirectory(_thumbnailCacheDirectory);
            string tempPath = Path.Combine(
                _thumbnailCacheDirectory,
                $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.tmp.jpg");
            try
            {
                long bytesWritten = 0;
                {
                    await using Stream input = await response.Content
                        .ReadAsStreamAsync(requestCancellation.Token)
                        .ConfigureAwait(false);
                    await using FileStream output = new(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(
                               buffer,
                               requestCancellation.Token).ConfigureAwait(false))
                           > 0)
                    {
                        bytesWritten += read;
                        if (bytesWritten > MaximumArtworkBytes)
                        {
                            break;
                        }

                        await output.WriteAsync(
                                buffer.AsMemory(0, read),
                                requestCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    await output.FlushAsync(requestCancellation.Token)
                        .ConfigureAwait(false);
                }

                if (bytesWritten is > 0 and <= MaximumArtworkBytes
                    && IsSafeArtworkFileForRole(
                        tempPath,
                        role,
                        budget,
                        cancellationToken,
                        out ArtworkContainerFormat containerFormat,
                        allowAnyAspect)
                    && DoesContentTypeMatchContainer(mediaType!, containerFormat))
                {
                    File.Move(tempPath, targetPath, overwrite: true);
                    return Path.GetFullPath(targetPath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
        }

        return null;
    }

    private string? FindSteamArtwork(
        string appId,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        foreach (string steamRoot in _steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string userdataRoot = Path.GetFullPath(
                Path.Combine(steamRoot, "userdata"));
            foreach (ArtworkFileSystemEntry entry in ReadSafeDirectoryEntries(
                         userdataRoot,
                         budget,
                         cancellationToken))
            {
                if (!entry.IsDirectory)
                {
                    continue;
                }

                string gridDir = Path.Combine(entry.Path, "config", "grid");
                if (!TryGetSafeFileSystemEntry(
                        gridDir,
                        out bool gridIsDirectory)
                    || !gridIsDirectory)
                {
                    continue;
                }

                string[] userGridCandidates = role is GameArtworkRole.Poster
                    ?
                    [
                        Path.Combine(gridDir, $"{appId}p.jpg"),
                        Path.Combine(gridDir, $"{appId}p.png"),
                        Path.Combine(gridDir, $"{appId}.jpg"),
                        Path.Combine(gridDir, $"{appId}.png"),
                    ]
                    :
                    [
                        Path.Combine(gridDir, $"{appId}_hero.jpg"),
                        Path.Combine(gridDir, $"{appId}_hero.png"),
                        Path.Combine(gridDir, $"{appId}.jpg"),
                        Path.Combine(gridDir, $"{appId}.png"),
                    ];

                string? foundGrid = FindFirstValidArtwork(
                    userGridCandidates,
                    role,
                    budget,
                    cancellationToken);
                if (foundGrid is not null)
                {
                    return foundGrid;
                }
            }

            string cacheRoot = Path.GetFullPath(
                Path.Combine(steamRoot, "appcache", "librarycache"));
            if (!TryGetSafeFileSystemEntry(
                    cacheRoot,
                    out bool cacheIsDirectory)
                || !cacheIsDirectory)
            {
                continue;
            }

            string appDirectory = Path.Combine(cacheRoot, appId);
            string[] preferredNames = role is GameArtworkRole.Poster
                ? ["library_600x900.jpg", "library_600x900.png", "library_capsule.jpg", "library_capsule.png"]
                : ["library_hero.jpg", "library_hero.png", "header.jpg", "header.png", "library_header.jpg", "library_header.png"];
            string[] preferredCandidates = preferredNames
                .SelectMany(fileName => new[]
                {
                    Path.Combine(appDirectory, fileName),
                    Path.Combine(cacheRoot, $"{appId}_{fileName}"),
                })
                .ToArray();
            string? preferred = FindFirstValidArtwork(
                preferredCandidates,
                role,
                budget,
                cancellationToken);
            preferred ??= FindArtworkInImmediateChildDirectories(
                appDirectory,
                preferredNames,
                role,
                budget,
                cancellationToken);
            if (preferred is not null)
            {
                return Path.GetFullPath(preferred);
            }

            string? discovered = FindBestImageInDirectory(
                appDirectory,
                normalizedGameName: string.Empty,
                maximumDepth: 1,
                role,
                budget,
                cancellationToken);
            if (discovered is not null)
            {
                return discovered;
            }
        }

        return null;
    }

    private static string? FindArtworkInImmediateChildDirectories(
        string rootDirectory,
        IReadOnlyList<string> preferredFileNames,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        ArtworkFileSystemEntry[] childDirectories =
            ReadSafeDirectoryEntries(
                    rootDirectory,
                    budget,
                    cancellationToken)
                .Where(entry => entry.IsDirectory)
                .ToArray();
        foreach (string fileName in preferredFileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ArtworkFileSystemEntry directory in childDirectories)
            {
                string candidate = Path.Combine(directory.Path, fileName);
                if (!budget.TryInspectName(cancellationToken))
                {
                    return null;
                }

                if (IsSafeArtworkFileForRole(
                        candidate,
                        role,
                        budget,
                        cancellationToken))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string? FindGameDirectoryArtwork(
        string installDirectory,
        string displayName,
        string executablePath,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        string normalizedGameName = NormalizeName(displayName);
        string? artwork = FindBestImageInDirectory(
            installDirectory,
            normalizedGameName,
            MaximumDirectoryDepth,
            role,
            budget,
            cancellationToken);
        if (artwork is not null)
        {
            return artwork;
        }

        string executableName = Path.GetFileNameWithoutExtension(
            executablePath);
        return FindBestImageInDirectory(
            installDirectory,
            NormalizeName(executableName),
            maximumDepth: 1,
            role,
            budget,
            cancellationToken);
    }

    private static string? FindBestImageInDirectory(
        string rootDirectory,
        string normalizedGameName,
        int maximumDepth,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        List<(string Path, long Score)> candidates = [];
        foreach (ArtworkEnumeratedFile discoveredFile in EnumerateSafeFiles(
                     rootDirectory,
                     maximumDepth,
                     budget,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = discoveredFile.Path;
            try
            {
                if (!IsSafeArtworkFile(file))
                {
                    continue;
                }

                string normalizedName = NormalizeName(
                    Path.GetFileNameWithoutExtension(file));
                long score = ScoreArtworkName(
                    normalizedName,
                    normalizedGameName,
                    role);
                score -= discoveredFile.Depth * 100L;
                score += Math.Min(
                    new FileInfo(file).Length / 1024,
                    2_000);
                candidates.Add((Path.GetFullPath(file), score));
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
            }
        }

        foreach ((string path, long _) in candidates
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.HasDecodeCapacity)
            {
                break;
            }

            if (IsSafeArtworkFileForRole(
                    path,
                    role,
                    budget,
                    cancellationToken))
            {
                return path;
            }
        }

        return null;
    }

    private static long ScoreArtworkName(
        string normalizedName,
        string normalizedGameName,
        GameArtworkRole role)
    {
        long score = 0;
        bool portraitName = normalizedName.Contains("library600x900", StringComparison.Ordinal)
            || normalizedName.Contains("portrait", StringComparison.Ordinal)
            || normalizedName.Contains("poster", StringComparison.Ordinal)
            || normalizedName.Contains("cover", StringComparison.Ordinal)
            || normalizedName.Contains("capsule", StringComparison.Ordinal);
        bool heroName = normalizedName.Contains("header", StringComparison.Ordinal)
            || normalizedName.Contains("hero", StringComparison.Ordinal)
            || normalizedName.Contains("banner", StringComparison.Ordinal);
        if ((role is GameArtworkRole.Poster && portraitName)
            || (role is GameArtworkRole.Hero && heroName))
        {
            score += 10_000;
        }
        else if (portraitName || heroName)
        {
            score += 1_000;
        }
        if (!string.IsNullOrEmpty(normalizedGameName)
            && normalizedName.Contains(
                normalizedGameName,
                StringComparison.Ordinal))
        {
            score += 4_000;
        }

        if (normalizedName.Contains("icon", StringComparison.Ordinal)
            || normalizedName.Contains("logo", StringComparison.Ordinal))
        {
            score -= 10_000;
        }

        if (normalizedName.Contains("screenshot", StringComparison.Ordinal)
            || normalizedName.Contains("wallpaper", StringComparison.Ordinal)
            || normalizedName.Contains("loading", StringComparison.Ordinal)
            || normalizedName.Contains("splash", StringComparison.Ordinal))
        {
            score -= 20_000;
        }

        return score;
    }

    private static bool HasDiscouragedArtworkName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedName = NormalizeName(
            Path.GetFileNameWithoutExtension(path));
        return normalizedName.Contains("icon", StringComparison.Ordinal)
            || normalizedName.Contains("logo", StringComparison.Ordinal)
            || normalizedName.Contains("screenshot", StringComparison.Ordinal)
            || normalizedName.Contains("wallpaper", StringComparison.Ordinal)
            || normalizedName.Contains("loading", StringComparison.Ordinal)
            || normalizedName.Contains("splash", StringComparison.Ordinal);
    }

    private static string? FindFirstValidArtwork(
        IEnumerable<string> candidates,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        foreach (string candidate in candidates)
        {
            if (!budget.TryInspectName(cancellationToken))
            {
                return null;
            }

            if (IsSafeArtworkFileForRole(
                    candidate,
                    role,
                    budget,
                    cancellationToken))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<ArtworkEnumeratedFile> EnumerateSafeFiles(
        string rootDirectory,
        int maximumDepth,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        if (maximumDepth < 0
            || !TryGetSafeFileSystemEntry(
                rootDirectory,
                out bool rootIsDirectory)
            || !rootIsDirectory)
        {
            yield break;
        }

        Queue<(string Directory, int Depth)> pending = [];
        pending.Enqueue((Path.GetFullPath(rootDirectory), 0));
        while (pending.TryDequeue(out (string Directory, int Depth) current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (ArtworkFileSystemEntry entry in ReadSafeDirectoryEntries(
                         current.Directory,
                         budget,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsDirectory)
                {
                    yield return new(entry.Path, current.Depth);
                    continue;
                }

                if (current.Depth >= maximumDepth)
                {
                    continue;
                }

                string name = Path.GetFileName(entry.Path);
                if (!name.Contains(
                        "screenshot",
                        StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(
                        "capture",
                        StringComparison.OrdinalIgnoreCase))
                {
                    pending.Enqueue((entry.Path, current.Depth + 1));
                }
            }

            if (!budget.HasNameCapacity)
            {
                yield break;
            }
        }
    }

    private static List<ArtworkFileSystemEntry>
        ReadSafeDirectoryEntries(
            string directory,
            ArtworkResolutionBudget budget,
            CancellationToken cancellationToken)
    {
        if (!budget.HasNameCapacity
            || !TryGetSafeFileSystemEntry(
                directory,
                out bool isDirectory)
            || !isDirectory)
        {
            return [];
        }

        List<ArtworkFileSystemEntry> entries = [];
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!budget.TryInspectName(cancellationToken))
                {
                    break;
                }

                if (TryGetSafeFileSystemEntry(path, out bool childIsDirectory))
                {
                    entries.Add(new(Path.GetFullPath(path), childIsDirectory));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
        }

        entries.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return entries;
    }

    private static bool TryGetSafeFileSystemEntry(
        string path,
        out bool isDirectory)
    {
        isDirectory = false;
        try
        {
            System.IO.FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            isDirectory =
                (attributes & System.IO.FileAttributes.Directory) != 0;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsSafeArtworkFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || Uri.TryCreate(path, UriKind.Absolute, out Uri? uri)
                && (!uri.IsFile || uri.IsUnc))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        if (!SupportedExtensions.Contains(extension))
        {
            return false;
        }

        try
        {
            FileInfo file = new(Path.GetFullPath(path));
            return file.Exists
                && file.Length is > 0 and <= MaximumArtworkBytes
                && (file.Attributes & System.IO.FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSafeArtworkFileForRole(
        string? path,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
        => IsSafeArtworkFileForRole(
            path,
            role,
            budget,
            cancellationToken,
            out _);

    private static bool IsSafeExecutableArtworkFallback(
        string? path,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken)
    {
        if (!IsSafeArtworkFile(path)
            || !budget.TryDecode(cancellationToken))
        {
            return false;
        }

        bool isValid = DetectArtworkContainer(path!)
                is not ArtworkContainerFormat.Unknown
            && TryGetImageDimensions(path!, out int width, out int height)
            && width >= 128
            && height >= 128;
        cancellationToken.ThrowIfCancellationRequested();
        return isValid;
    }

    private static bool IsSafeArtworkFileForRole(
        string? path,
        GameArtworkRole role,
        ArtworkResolutionBudget budget,
        CancellationToken cancellationToken,
        out ArtworkContainerFormat containerFormat,
        bool allowAnyAspect = false)
    {
        containerFormat = ArtworkContainerFormat.Unknown;
        if (!IsSafeArtworkFile(path)
            || !budget.TryDecode(cancellationToken))
        {
            return false;
        }

        containerFormat = DetectArtworkContainer(path!);
        bool isValid = containerFormat is not ArtworkContainerFormat.Unknown
            && TryGetImageDimensions(
                path!,
                out int width,
                out int height)
            && width >= 128
            && height >= 128
            && (allowAnyAspect || MatchesArtworkRole(width, height, role));
        cancellationToken.ThrowIfCancellationRequested();
        return isValid;
    }

    private static ArtworkContainerFormat DetectArtworkContainer(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[12];
            using FileStream input = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            int read = input.ReadAtLeast(
                header,
                minimumBytes: 4,
                throwOnEndOfStream: false);
            ReadOnlySpan<byte> content = header[..read];
            if (content.StartsWith(PngSignature))
            {
                return ArtworkContainerFormat.Png;
            }

            if (content.Length >= 3
                && content[0] == 0xFF
                && content[1] == 0xD8
                && content[2] == 0xFF)
            {
                return ArtworkContainerFormat.Jpeg;
            }

            if (content.StartsWith("BM"u8))
            {
                return ArtworkContainerFormat.Bmp;
            }

            if (content.StartsWith("GIF87a"u8)
                || content.StartsWith("GIF89a"u8))
            {
                return ArtworkContainerFormat.Gif;
            }

            if (content.Length >= 4
                && content[0] == 0x00
                && content[1] == 0x00
                && content[2] == 0x01
                && content[3] == 0x00)
            {
                return ArtworkContainerFormat.Icon;
            }

            if (content.Length >= 12
                && content[..4].SequenceEqual("RIFF"u8)
                && content[8..12].SequenceEqual("WEBP"u8))
            {
                return ArtworkContainerFormat.Webp;
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
        }

        return ArtworkContainerFormat.Unknown;
    }

    private static bool TryGetImageDimensions(
        string path,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        try
        {
            StorageFile file = WaitBounded(
                StorageFile.GetFileFromPathAsync(path),
                FileAccessTimeout);
            using IRandomAccessStream stream = WaitBounded(
                file.OpenReadAsync(),
                FileAccessTimeout);
            BitmapDecoder decoder = WaitBounded(
                BitmapDecoder.CreateAsync(stream),
                DecodeTimeout);
            width = checked((int)decoder.PixelWidth);
            height = checked((int)decoder.PixelHeight);
            if (width <= 0 || height <= 0
                || (long)width * height > 16L * 1024 * 1024)
            {
                return false;
            }

            BitmapTransform transform = new()
            {
                ScaledWidth = checked((uint)width),
                ScaledHeight = checked((uint)height),
            };
            PixelDataProvider pixels = WaitBounded(
                decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage),
                DecodeTimeout);
            byte[] pixelData = pixels.DetachPixelData();
            return pixelData.LongLength == (long)width * height * 4
                && HasValidPngPixelPayload(path, width, height);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or TimeoutException)
        {
        }
        catch (COMException)
        {
        }

        return false;
    }

    /// <summary>
    /// Jak dlugo wolno czekac na dekoder systemowy. PNG z poprawnym
    /// naglowkiem i uszkodzonymi danymi pikseli potrafi zawiesic
    /// GetPixelDataAsync w WIC na zawsze (zaobserwowane 2026-09-16: host
    /// testow stal 35 minut przy 7 s CPU). Bez limitu synchronizacja
    /// biblioteki nigdy by sie nie skonczyla; z limitem obraz jest
    /// odrzucany, a resolver idzie dalej po nastepne zrodlo.
    /// </summary>
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FileAccessTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Czeka na operacje WinRT najwyzej <paramref name="timeout"/>; po
    /// przekroczeniu prosi o anulowanie i rzuca <see cref="TimeoutException"/>.
    /// Zawieszona operacja moze zostac w tle — to i tak lepsze niz
    /// zawieszony watek, ktory nikomu juz nie odda sterowania.
    /// </summary>
    internal static T WaitBounded<T>(
        IAsyncOperation<T> operation,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task<T> task = operation.AsTask();
        Task completed = Task.WhenAny(task, Task.Delay(timeout))
            .GetAwaiter()
            .GetResult();
        if (!ReferenceEquals(completed, task))
        {
            try
            {
                operation.Cancel();
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or COMException)
            {
            }

            throw new TimeoutException(
                "Operacja obrazu nie zakończyła się w limicie "
                + $"{timeout.TotalSeconds:0} s.");
        }

        return task.GetAwaiter().GetResult();
    }

    private static bool HasValidPngPixelPayload(
        string path,
        int decodedWidth,
        int decodedHeight)
    {
        byte[] content = File.ReadAllBytes(path);
        if (!content.AsSpan().StartsWith(PngSignature))
        {
            return true;
        }

        int offset = PngSignature.Length;
        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        bool hasHeader = false;
        bool hasImageData = false;
        bool hasEnd = false;
        using MemoryStream compressed = new();
        while (offset <= content.Length - 12)
        {
            uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(
                content.AsSpan(offset, 4));
            if (unsignedLength > int.MaxValue)
            {
                return false;
            }

            int chunkLength = (int)unsignedLength;
            int dataOffset = checked(offset + 8);
            int nextChunk = checked(dataOffset + chunkLength + 4);
            if (nextChunk > content.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> type = content.AsSpan(offset + 4, 4);
            ReadOnlySpan<byte> data = content.AsSpan(dataOffset, chunkLength);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (hasHeader || chunkLength != 13)
                {
                    return false;
                }

                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                    data[4..]));
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
                hasHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                compressed.Write(data);
                hasImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (chunkLength != 0)
                {
                    return false;
                }

                hasEnd = true;
                offset = nextChunk;
                break;
            }

            offset = nextChunk;
        }

        if (!hasHeader
            || !hasImageData
            || !hasEnd
            || offset != content.Length
            || width != decodedWidth
            || height != decodedHeight
            || !TryGetExpectedPngPayloadBytes(
                width,
                height,
                bitDepth,
                colorType,
                interlace,
                out long expectedBytes))
        {
            return false;
        }

        compressed.Position = 0;
        using ZLibStream inflater = new(
            compressed,
            CompressionMode.Decompress,
            leaveOpen: false);
        byte[] buffer = new byte[64 * 1024];
        long decompressedBytes = 0;
        int read;
        while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
        {
            decompressedBytes += read;
            if (decompressedBytes > expectedBytes)
            {
                return false;
            }
        }

        return decompressedBytes == expectedBytes;
    }

    private static bool TryGetExpectedPngPayloadBytes(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte interlace,
        out long expectedBytes)
    {
        expectedBytes = 0;
        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };
        if (width <= 0
            || height <= 0
            || channels == 0
            || bitDepth is not (1 or 2 or 4 or 8 or 16)
            || interlace > 1)
        {
            return false;
        }

        try
        {
            int bitsPerPixel = checked(channels * bitDepth);
            expectedBytes = interlace == 0
                ? GetPngPassBytes(width, height, bitsPerPixel)
                : GetPngPassBytes(width, height, bitsPerPixel, 0, 0, 8, 8)
                    + GetPngPassBytes(width, height, bitsPerPixel, 4, 0, 8, 8)
                    + GetPngPassBytes(width, height, bitsPerPixel, 0, 4, 4, 8)
                    + GetPngPassBytes(width, height, bitsPerPixel, 2, 0, 4, 4)
                    + GetPngPassBytes(width, height, bitsPerPixel, 0, 2, 2, 4)
                    + GetPngPassBytes(width, height, bitsPerPixel, 1, 0, 2, 2)
                    + GetPngPassBytes(width, height, bitsPerPixel, 0, 1, 1, 2);
            return expectedBytes > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static long GetPngPassBytes(
        int width,
        int height,
        int bitsPerPixel,
        int startX = 0,
        int startY = 0,
        int stepX = 1,
        int stepY = 1)
    {
        long passWidth = width <= startX
            ? 0
            : ((long)width - startX + stepX - 1) / stepX;
        long passHeight = height <= startY
            ? 0
            : ((long)height - startY + stepY - 1) / stepY;
        return passWidth == 0 || passHeight == 0
            ? 0
            : checked(passHeight * (1 + ((passWidth * bitsPerPixel + 7) / 8)));
    }

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsAllowedSteamHost(Uri? uri) =>
        uri is not null
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && (uri.Host.Equals(
                "shared.fastly.steamstatic.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(
                "cdn.cloudflare.steamstatic.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(
                "steamcdn-a.akamaihd.net",
                StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedEpicHost(Uri? uri) =>
        uri is not null
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(
            "cdn1.epicgames.com",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedImageContentType(string? contentType) =>
        contentType is not null
        && (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            || contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase));

    private static bool DoesContentTypeMatchContainer(
        string contentType,
        ArtworkContainerFormat containerFormat) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            ? containerFormat is ArtworkContainerFormat.Jpeg
            : contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                ? containerFormat is ArtworkContainerFormat.Png
                : contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                    && containerFormat is ArtworkContainerFormat.Webp;

    private static bool IsNumericAppId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(char.IsAsciiDigit);

    private static bool IsEpicSource(string source) =>
        source.Equals("Epic", StringComparison.OrdinalIgnoreCase)
        || source.Equals("Epic Games", StringComparison.OrdinalIgnoreCase);

    private static bool IsEpicCatalogId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool MatchesArtworkRole(
        int width,
        int height,
        GameArtworkRole role) =>
        role is GameArtworkRole.Poster
            ? height * 100L >= width * 115L
            : width * 100L >= height * 115L;

    private static bool IsReadableEpicCatalogFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists
                && file.Length is > 0 and <= MaximumEpicCatalogBytes
                && (file.Attributes & System.IO.FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static string NormalizeName(string value) =>
        new(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static HashSet<string> FindDefaultSteamRoots()
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        AddExistingDirectory(
            roots,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                "Steam"));
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Valve\Steam");
            AddExistingDirectory(roots, key?.GetValue("SteamPath") as string);
        }
        catch (Exception exception) when (
            exception is
                UnauthorizedAccessException
                or System.Security.SecurityException
                or IOException)
        {
        }

        return roots;
    }

    private static IEnumerable<string> FindDefaultEpicCatalogPaths()
    {
        string commonApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(commonApplicationData))
        {
            yield return Path.Combine(
                commonApplicationData,
                "Epic",
                "EpicGamesLauncher",
                "Data",
                "Catalog",
                "catcache.bin");
        }
    }

    private static string[] FindDefaultEpicManifestPaths()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.item", SearchOption.TopDirectoryOnly).Take(256).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static void AddExistingDirectory(
        HashSet<string> output,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)
            && Directory.Exists(path))
        {
            output.Add(Path.GetFullPath(path));
        }
    }

    private static readonly HashSet<string> SupportedExtensions = new(
        [".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);

    private readonly record struct ArtworkFileSystemEntry(
        string Path,
        bool IsDirectory);

    private readonly record struct ArtworkEnumeratedFile(
        string Path,
        int Depth);

    private sealed record EpicCatalogArtwork(Uri Url, int Score);

    private enum ArtworkContainerFormat
    {
        Unknown,
        Bmp,
        Gif,
        Icon,
        Jpeg,
        Png,
        Webp,
    }

    private sealed class ArtworkResolutionBudget(
        GameArtworkRole role,
        IArtworkResolutionObserver? observer)
    {
        private int _decodedImages;
        private int _inspectedNames;

        internal bool HasDecodeCapacity =>
            _decodedImages < 32;

        internal bool HasNameCapacity =>
            _inspectedNames < MaximumCandidateFiles;

        internal bool TryInspectName(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasNameCapacity)
            {
                return false;
            }

            _inspectedNames++;
            observer?.OnBudgetChanged(
                role,
                _inspectedNames,
                _decodedImages);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }

        internal bool TryDecode(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasDecodeCapacity)
            {
                return false;
            }

            _decodedImages++;
            observer?.OnBudgetChanged(
                role,
                _inspectedNames,
                _decodedImages);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

}

internal interface IArtworkResolutionObserver
{
    void OnBudgetChanged(
        GameArtworkRole role,
        int inspectedNames,
        int decodedImages);
}

internal interface IExecutableArtworkExtractor
{
    ValueTask<string?> ExtractAsync(
        string executablePath,
        string cacheDirectory,
        CancellationToken cancellationToken);
}

internal sealed class WindowsExecutableArtworkExtractor :
    IExecutableArtworkExtractor
{
    private const ulong MaximumThumbnailBytes = 8UL * 1024 * 1024;

    public async ValueTask<string?> ExtractAsync(
        string executablePath,
        string cacheDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(executablePath)
            || !Path.GetExtension(executablePath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string cacheKey = CreateCacheKey(executablePath);
        foreach (string extension in new[]
                 {
                     ".png",
                     ".jpg",
                     ".bmp",
                     ".ico",
                 })
        {
            string cached = Path.Combine(cacheDirectory, cacheKey + extension);
            if (LocalGameArtworkResolver.IsSafeArtworkFile(cached))
            {
                return cached;
            }
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(
                Path.GetFullPath(executablePath));
            using StorageItemThumbnail thumbnail =
                await file.GetThumbnailAsync(
                    ThumbnailMode.SingleItem,
                    requestedSize: 512,
                    ThumbnailOptions.ResizeThumbnail);
            cancellationToken.ThrowIfCancellationRequested();
            if (thumbnail.Size is 0 or > MaximumThumbnailBytes)
            {
                return null;
            }

            uint byteCount = checked((uint)thumbnail.Size);
            byte[] bytes = new byte[byteCount];
            using DataReader reader = new(thumbnail.GetInputStreamAt(0));
            uint loaded = await reader.LoadAsync(byteCount);
            if (loaded != byteCount)
            {
                return null;
            }

            reader.ReadBytes(bytes);
            string extension = GetExtension(thumbnail.ContentType, bytes);
            Directory.CreateDirectory(cacheDirectory);
            string destination = Path.Combine(
                cacheDirectory,
                cacheKey + extension);
            string temporary = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(
                    temporary,
                    bytes,
                    cancellationToken);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return LocalGameArtworkResolver.IsSafeArtworkFile(destination)
                ? destination
                : null;
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or ArgumentException
                or COMException
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string CreateCacheKey(string executablePath)
    {
        FileInfo file = new(executablePath);
        string identity = $"{Path.GetFullPath(executablePath)}|"
            + $"{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    private static string GetExtension(
        string? contentType,
        ReadOnlySpan<byte> content)
    {
        if (string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith(PngSignature))
        {
            return ".png";
        }

        if (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith(JpegSignature))
        {
            return ".jpg";
        }

        if (string.Equals(contentType, "image/x-icon", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith(IconSignature))
        {
            return ".ico";
        }

        return ".bmp";
    }

    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> IconSignature => [0x00, 0x00, 0x01, 0x00];
}

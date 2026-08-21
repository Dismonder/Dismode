using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace GameShift.Windows.Profiles;

public sealed class LocalGameArtworkResolver
{
    private const long MaximumArtworkBytes = 32L * 1024 * 1024;
    private const int MaximumCandidateFiles = 256;
    private const int MaximumDirectoryDepth = 2;
    private readonly string[] _steamRoots;
    private readonly string _thumbnailCacheDirectory;
    private readonly IExecutableArtworkExtractor _executableExtractor;

    public LocalGameArtworkResolver(
        IEnumerable<string>? steamRoots = null,
        string? thumbnailCacheDirectory = null)
        : this(
            steamRoots,
            thumbnailCacheDirectory,
            new WindowsExecutableArtworkExtractor())
    {
    }

    internal LocalGameArtworkResolver(
        IEnumerable<string>? steamRoots,
        string? thumbnailCacheDirectory,
        IExecutableArtworkExtractor executableExtractor)
    {
        ArgumentNullException.ThrowIfNull(executableExtractor);
        _steamRoots = (steamRoots ?? FindDefaultSteamRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _thumbnailCacheDirectory = Path.GetFullPath(
            thumbnailCacheDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "GameShift",
                "Artwork"));
        _executableExtractor = executableExtractor;
    }

    public async ValueTask<GameArtwork?> ResolveAsync(
        DetectedGame game,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        cancellationToken.ThrowIfCancellationRequested();
        string executablePath = Path.GetFullPath(game.ExecutablePath);
        if (!File.Exists(executablePath)
            || !Path.GetExtension(executablePath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (game.Source.Equals(
                "Steam",
                StringComparison.OrdinalIgnoreCase)
            && IsNumericAppId(game.ExternalId)
            && FindSteamArtwork(game.ExternalId) is string steamArtwork)
        {
            return new(
                steamArtwork,
                GameArtworkSource.SteamLibraryCache);
        }

        string installDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException(
                "The game executable has no installation directory.");
        if (!game.Source.Equals(
                "Roblox",
                StringComparison.OrdinalIgnoreCase)
            && FindGameDirectoryArtwork(
                    installDirectory,
                    game.DisplayName,
                    executablePath,
                    cancellationToken) is string localArtwork)
        {
            return new(localArtwork, GameArtworkSource.GameDirectory);
        }

        string? extracted = await _executableExtractor.ExtractAsync(
                executablePath,
                _thumbnailCacheDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsSafeArtworkFile(extracted))
        {
            return null;
        }

        return new(
            Path.GetFullPath(extracted!),
            GameArtworkSource.ExecutableThumbnail);
    }

    private string? FindSteamArtwork(string appId)
    {
        foreach (string steamRoot in _steamRoots)
        {
            string userdataRoot = Path.GetFullPath(
                Path.Combine(steamRoot, "userdata"));
            if (Directory.Exists(userdataRoot))
            {
                try
                {
                    foreach (string userDir in Directory.EnumerateDirectories(
                                 userdataRoot,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        string gridDir = Path.Combine(userDir, "config", "grid");
                        if (!Directory.Exists(gridDir))
                        {
                            continue;
                        }

                        string[] userGridCandidates =
                        [
                            Path.Combine(gridDir, $"{appId}_hero.jpg"),
                            Path.Combine(gridDir, $"{appId}_hero.png"),
                            Path.Combine(gridDir, $"{appId}p.jpg"),
                            Path.Combine(gridDir, $"{appId}p.png"),
                            Path.Combine(gridDir, $"{appId}.jpg"),
                            Path.Combine(gridDir, $"{appId}.png"),
                            Path.Combine(gridDir, $"{appId}_logo.png"),
                        ];

                        string? foundGrid = userGridCandidates.FirstOrDefault(
                            IsSafeArtworkFile);
                        if (foundGrid is not null)
                        {
                            return Path.GetFullPath(foundGrid);
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException)
                {
                }
            }

            string cacheRoot = Path.GetFullPath(
                Path.Combine(steamRoot, "appcache", "librarycache"));
            if (!Directory.Exists(cacheRoot))
            {
                continue;
            }

            string appDirectory = Path.Combine(cacheRoot, appId);
            string[] wideCandidates =
            [
                Path.Combine(appDirectory, "library_hero.jpg"),
                Path.Combine(appDirectory, "library_hero.png"),
                Path.Combine(appDirectory, "header.jpg"),
                Path.Combine(appDirectory, "header.png"),
                Path.Combine(appDirectory, "library_header.jpg"),
                Path.Combine(appDirectory, "library_header.png"),
                Path.Combine(cacheRoot, $"{appId}_library_hero.jpg"),
                Path.Combine(cacheRoot, $"{appId}_library_hero.png"),
                Path.Combine(cacheRoot, $"{appId}_header.jpg"),
                Path.Combine(cacheRoot, $"{appId}_header.png"),
                Path.Combine(cacheRoot, $"{appId}_library_header.jpg"),
                Path.Combine(cacheRoot, $"{appId}_library_header.png"),
            ];
            string? wide = wideCandidates.FirstOrDefault(
                IsSafeArtworkFile);
            wide ??= FindArtworkInImmediateChildDirectories(
                appDirectory,
                [
                    "library_hero.jpg",
                    "library_hero.png",
                    "header.jpg",
                    "header.png",
                    "library_header.jpg",
                    "library_header.png",
                ]);
            if (wide is not null)
            {
                return Path.GetFullPath(wide);
            }

            string[] portraitCandidates =
            [
                Path.Combine(appDirectory, "library_600x900.jpg"),
                Path.Combine(appDirectory, "library_600x900.png"),
                Path.Combine(appDirectory, "library_capsule.jpg"),
                Path.Combine(appDirectory, "library_capsule.png"),
                Path.Combine(cacheRoot, $"{appId}_library_600x900.jpg"),
                Path.Combine(cacheRoot, $"{appId}_library_600x900.png"),
                Path.Combine(cacheRoot, $"{appId}_library_capsule.jpg"),
                Path.Combine(cacheRoot, $"{appId}_library_capsule.png"),
            ];
            string? portrait = portraitCandidates.FirstOrDefault(
                IsSafeArtworkFile);
            portrait ??= FindArtworkInImmediateChildDirectories(
                appDirectory,
                [
                    "library_600x900.jpg",
                    "library_600x900.png",
                    "library_capsule.jpg",
                    "library_capsule.png",
                ]);
            if (portrait is not null)
            {
                return Path.GetFullPath(portrait);
            }

            string? discovered = FindBestImageInDirectory(
                appDirectory,
                normalizedGameName: string.Empty,
                maximumDepth: 1,
                CancellationToken.None);
            if (discovered is not null)
            {
                return discovered;
            }
        }

        return null;
    }

    private static string? FindArtworkInImmediateChildDirectories(
        string rootDirectory,
        IReadOnlyList<string> preferredFileNames)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        try
        {
            string[] childDirectories = Directory.EnumerateDirectories(
                    rootDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Take(MaximumCandidateFiles)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (string fileName in preferredFileNames)
            {
                string? candidate = childDirectories
                    .Select(directory => Path.Combine(directory, fileName))
                    .FirstOrDefault(IsSafeArtworkFile);
                if (candidate is not null)
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
        }

        return null;
    }

    private static string? FindGameDirectoryArtwork(
        string installDirectory,
        string displayName,
        string executablePath,
        CancellationToken cancellationToken)
    {
        string normalizedGameName = NormalizeName(displayName);
        string? artwork = FindBestImageInDirectory(
            installDirectory,
            normalizedGameName,
            MaximumDirectoryDepth,
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
            cancellationToken);
    }

    private static string? FindBestImageInDirectory(
        string rootDirectory,
        string normalizedGameName,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return null;
        }

        List<(string Path, long Score)> candidates = [];
        Queue<(string Directory, int Depth)> pending = [];
        pending.Enqueue((Path.GetFullPath(rootDirectory), 0));
        int inspected = 0;
        while (pending.TryDequeue(out (string Directory, int Depth) current)
            && inspected < MaximumCandidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (string file in Directory.EnumerateFiles(
                             current.Directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (++inspected > MaximumCandidateFiles)
                    {
                        break;
                    }

                    if (!IsSafeArtworkFile(file))
                    {
                        continue;
                    }

                    string normalizedName = NormalizeName(
                        Path.GetFileNameWithoutExtension(file));
                    long score = ScoreArtworkName(
                        normalizedName,
                        normalizedGameName);
                    score -= current.Depth * 100L;
                    score += Math.Min(
                        new FileInfo(file).Length / 1024,
                        2_000);
                    candidates.Add((Path.GetFullPath(file), score));
                }

                if (current.Depth >= maximumDepth)
                {
                    continue;
                }

                foreach (string child in Directory.EnumerateDirectories(
                             current.Directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(child);
                    if (!name.Contains(
                            "screenshot",
                            StringComparison.OrdinalIgnoreCase)
                        && !name.Contains(
                            "capture",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Enqueue((child, current.Depth + 1));
                    }
                }
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
            }
        }

        return candidates
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static long ScoreArtworkName(
        string normalizedName,
        string normalizedGameName)
    {
        long score = 0;
        if (normalizedName.Contains("library600x900", StringComparison.Ordinal)
            || normalizedName.Contains("portrait", StringComparison.Ordinal)
            || normalizedName.Contains("poster", StringComparison.Ordinal))
        {
            score += 10_000;
        }
        else if (normalizedName.Contains("cover", StringComparison.Ordinal)
            || normalizedName.Contains("capsule", StringComparison.Ordinal))
        {
            score += 8_000;
        }
        else if (normalizedName.Contains("header", StringComparison.Ordinal)
            || normalizedName.Contains("hero", StringComparison.Ordinal)
            || normalizedName.Contains("banner", StringComparison.Ordinal))
        {
            score += 6_000;
        }
        else if (normalizedName.Contains("icon", StringComparison.Ordinal)
            || normalizedName.Contains("logo", StringComparison.Ordinal))
        {
            score += 3_000;
        }

        if (!string.IsNullOrEmpty(normalizedGameName)
            && normalizedName.Contains(
                normalizedGameName,
                StringComparison.Ordinal))
        {
            score += 4_000;
        }

        if (normalizedName.Contains("screenshot", StringComparison.Ordinal)
            || normalizedName.Contains("wallpaper", StringComparison.Ordinal)
            || normalizedName.Contains("loading", StringComparison.Ordinal))
        {
            score -= 20_000;
        }

        return score;
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
                && file.Length is > 0 and <= MaximumArtworkBytes;
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

    private static bool IsNumericAppId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(char.IsAsciiDigit);

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

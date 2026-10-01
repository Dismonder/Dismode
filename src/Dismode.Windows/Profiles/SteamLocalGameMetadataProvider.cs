using System.Globalization;
using System.Text;
using Dismode.Core.Profiles;
using Microsoft.Win32;

namespace Dismode.Windows.Profiles;

public sealed class SteamLocalGameMetadataProvider : IGameMetadataProvider
{
    private const long MaximumManifestBytes = 2L * 1024 * 1024;
    private const long MaximumLocalConfigBytes = 8L * 1024 * 1024;
    private readonly string[] _steamRoots;

    public SteamLocalGameMetadataProvider(
        IEnumerable<string>? steamRoots = null)
    {
        _steamRoots = (steamRoots ?? FindDefaultSteamRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Source => "Steam";

    public ValueTask<GameMetadataProviderResult?> ReadAsync(
        ManualGameProfile profile,
        string? externalId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsNumericAppId(externalId)
            || !File.Exists(profile.ExecutablePath))
        {
            return ValueTask.FromResult<GameMetadataProviderResult?>(null);
        }

        string appId = externalId!;
        SteamManifestMetadata manifest = ReadManifest(appId);
        SteamUsageMetadata? usage = ReadMostRecentUsage(
            appId,
            cancellationToken);
        DateTimeOffset? lastPlayedAtUtc = MaxTimestamp(
            ToTimestamp(manifest.LastPlayedUnixSeconds),
            ToTimestamp(usage?.LastPlayedUnixSeconds));

        string? launcherPath = NormalizeLauncherPath(
            manifest.LauncherPath);
        if (manifest.WasFound is false
            && usage is null)
        {
            return ValueTask.FromResult<GameMetadataProviderResult?>(null);
        }

        return ValueTask.FromResult<GameMetadataProviderResult?>(
            new(
                launcherPath,
                lastPlayedAtUtc,
                usage?.PlaytimeMinutes,
                HeroArtworkPath: null));
    }

    private SteamManifestMetadata ReadManifest(string appId)
    {
        foreach (string library in FindSteamLibraries())
        {
            string manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{appId}.acf");
            if (!IsReadableFile(manifestPath, MaximumManifestBytes))
            {
                continue;
            }

            string? launcherPath = null;
            long? lastPlayed = null;
            try
            {
                ScanPairs(
                    manifestPath,
                    MaximumManifestBytes,
                    (sections, key, value) =>
                    {
                        if (sections.Count == 0
                            || !sections[^1].Equals(
                                "AppState",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        if (key.Equals(
                                "LauncherPath",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            launcherPath = value;
                        }
                        else if (key.Equals(
                                     "LastPlayed",
                                     StringComparison.OrdinalIgnoreCase)
                                 && TryReadNonNegativeInt64(
                                     value,
                                     out long parsed))
                        {
                            lastPlayed = parsed;
                        }
                    });
                return new(true, launcherPath, lastPlayed);
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or InvalidDataException)
            {
            }
        }

        return new(false, null, null);
    }

    private SteamUsageMetadata? ReadMostRecentUsage(
        string appId,
        CancellationToken cancellationToken)
    {
        List<SteamUsageMetadata> candidates = [];
        foreach (string steamRoot in _steamRoots)
        {
            string userDataDirectory = Path.Combine(
                steamRoot,
                "userdata");
            if (!Directory.Exists(userDataDirectory))
            {
                continue;
            }

            IEnumerable<string> localConfigs;
            try
            {
                localConfigs = Directory.EnumerateDirectories(
                        userDataDirectory,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(directory => Path.Combine(
                        directory,
                        "config",
                        "localconfig.vdf"))
                    .Where(path => IsReadableFile(
                        path,
                        MaximumLocalConfigBytes))
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
                continue;
            }

            foreach (string localConfig in localConfigs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SteamUsageMetadata? usage = ReadUsage(localConfig, appId);
                if (usage is not null)
                {
                    candidates.Add(usage);
                }
            }
        }

        return candidates
            .OrderByDescending(candidate =>
                candidate.LastPlayedUnixSeconds ?? -1)
            .ThenByDescending(candidate => candidate.PlaytimeMinutes ?? -1)
            .FirstOrDefault();
    }

    private static SteamUsageMetadata? ReadUsage(
        string localConfigPath,
        string appId)
    {
        long? lastPlayed = null;
        long? playtime = null;
        try
        {
            ScanPairs(
                localConfigPath,
                MaximumLocalConfigBytes,
                (sections, key, value) =>
                {
                    if (sections.Count < 2
                        || !sections[^2].Equals(
                            "apps",
                            StringComparison.OrdinalIgnoreCase)
                        || !sections[^1].Equals(
                            appId,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (key.Equals(
                            "LastPlayed",
                            StringComparison.OrdinalIgnoreCase)
                        && TryReadNonNegativeInt64(value, out long parsed))
                    {
                        lastPlayed = parsed;
                    }
                    else if (key.Equals(
                                 "Playtime",
                                 StringComparison.OrdinalIgnoreCase)
                             && TryReadNonNegativeInt64(
                                 value,
                                 out parsed))
                    {
                        playtime = parsed;
                    }
                });
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or InvalidDataException)
        {
            return null;
        }

        return lastPlayed is null && playtime is null
            ? null
            : new(lastPlayed, playtime);
    }

    private HashSet<string> FindSteamLibraries()
    {
        HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string steamRoot in _steamRoots)
        {
            libraries.Add(steamRoot);
            string libraryFolders = Path.Combine(
                steamRoot,
                "steamapps",
                "libraryfolders.vdf");
            if (!IsReadableFile(libraryFolders, MaximumManifestBytes))
            {
                continue;
            }

            try
            {
                ScanPairs(
                    libraryFolders,
                    MaximumManifestBytes,
                    (_, key, value) =>
                    {
                        if (!key.Equals(
                                "path",
                                StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrWhiteSpace(value))
                        {
                            return;
                        }

                        string normalized = Path.GetFullPath(value);
                        if (Directory.Exists(normalized))
                        {
                            libraries.Add(normalized);
                        }
                    });
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or InvalidDataException
                    or ArgumentException
                    or NotSupportedException)
            {
            }
        }

        return libraries;
    }

    private static void ScanPairs(
        string path,
        long maximumBytes,
        Action<IReadOnlyList<string>, string, string> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (!IsReadableFile(path, maximumBytes))
        {
            throw new InvalidDataException(
                "The Steam metadata file is unavailable or too large.");
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        VdfTokenReader tokens = new(reader);
        List<string> sections = [];
        while (tokens.Read() is string key)
        {
            if (key == "}")
            {
                if (sections.Count > 0)
                {
                    sections.RemoveAt(sections.Count - 1);
                }

                continue;
            }

            if (key == "{")
            {
                throw new InvalidDataException(
                    "The Steam metadata contains an unexpected section.");
            }

            string valueOrSection = tokens.Read()
                ?? throw new InvalidDataException(
                    "The Steam metadata contains an incomplete pair.");
            if (valueOrSection == "{")
            {
                sections.Add(key);
                continue;
            }

            if (valueOrSection == "}")
            {
                throw new InvalidDataException(
                    "The Steam metadata contains an incomplete value.");
            }

            visitor(sections, key, valueOrSection);
        }
    }

    private static bool IsReadableFile(string path, long maximumBytes)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists && file.Length is > 0 && file.Length <= maximumBytes;
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

    private static bool TryReadNonNegativeInt64(
        string value,
        out long parsed) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out parsed)
        && parsed >= 0;

    private static DateTimeOffset? ToTimestamp(long? unixSeconds)
    {
        if (unixSeconds is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? MaxTimestamp(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null
            ? second
            : second is null || first >= second
                ? first
                : second;

    private static string? NormalizeLauncherPath(string? launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath)
            || !Path.IsPathFullyQualified(launcherPath)
            || launcherPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(launcherPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsNumericAppId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(char.IsAsciiDigit);

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

    private sealed class VdfTokenReader(TextReader reader)
    {
        private int _buffered = -1;

        internal string? Read()
        {
            int current = ReadNonWhitespaceAndComments();
            if (current < 0)
            {
                return null;
            }

            if (current is '{' or '}')
            {
                return ((char)current).ToString();
            }

            if (current == '"')
            {
                return ReadQuoted();
            }

            StringBuilder value = new();
            value.Append((char)current);
            while (Peek() is int next
                && next >= 0
                && !char.IsWhiteSpace((char)next)
                && next is not '{' and not '}')
            {
                value.Append((char)Take());
            }

            return value.ToString();
        }

        private string ReadQuoted()
        {
            StringBuilder value = new();
            while (Take() is int current && current >= 0)
            {
                if (current == '"')
                {
                    return value.ToString();
                }

                if (current == '\\')
                {
                    int escaped = Take();
                    if (escaped < 0)
                    {
                        break;
                    }

                    value.Append(escaped switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => (char)escaped,
                    });
                    continue;
                }

                value.Append((char)current);
            }

            throw new InvalidDataException(
                "The Steam metadata contains an unterminated string.");
        }

        private int ReadNonWhitespaceAndComments()
        {
            while (Take() is int current && current >= 0)
            {
                if (char.IsWhiteSpace((char)current))
                {
                    continue;
                }

                if (current == '/' && Peek() == '/')
                {
                    _ = Take();
                    while (Take() is int comment && comment >= 0
                        && comment != '\n')
                    {
                    }

                    continue;
                }

                return current;
            }

            return -1;
        }

        private int Peek()
        {
            if (_buffered < 0)
            {
                _buffered = reader.Read();
            }

            return _buffered;
        }

        private int Take()
        {
            if (_buffered >= 0)
            {
                int current = _buffered;
                _buffered = -1;
                return current;
            }

            return reader.Read();
        }
    }

    private sealed record SteamManifestMetadata(
        bool WasFound,
        string? LauncherPath,
        long? LastPlayedUnixSeconds);

    private sealed record SteamUsageMetadata(
        long? LastPlayedUnixSeconds,
        long? PlaytimeMinutes);
}

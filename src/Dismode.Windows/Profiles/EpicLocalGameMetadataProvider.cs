using System.Globalization;
using System.Text;
using Dismode.Core.Profiles;

namespace Dismode.Windows.Profiles;

public sealed class EpicLocalGameMetadataProvider : IGameMetadataProvider
{
    private const string LastPlayedPrefix = "LastPlayedGame=";
    private const long MaximumSettingsBytes = 4L * 1024 * 1024;
    private readonly string[] _launcherPaths;
    private readonly string[] _settingsPaths;

    public EpicLocalGameMetadataProvider(
        IEnumerable<string>? settingsPaths = null,
        IEnumerable<string>? launcherPaths = null)
    {
        _settingsPaths = NormalizeCandidatePaths(
            settingsPaths ?? FindDefaultSettingsPaths());
        _launcherPaths = NormalizeCandidatePaths(
            launcherPaths ?? FindDefaultLauncherPaths());
    }

    public string Source => "Epic Games";

    public async ValueTask<GameMetadataProviderResult?> ReadAsync(
        ManualGameProfile profile,
        string? externalId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(externalId)
            || !File.Exists(profile.ExecutablePath))
        {
            return null;
        }

        string catalogItemId = externalId.Trim();
        DateTimeOffset? lastPlayedAtUtc = null;
        foreach (string settingsPath in _settingsPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset? candidate = await ReadLatestLastPlayedAsync(
                    settingsPath,
                    catalogItemId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null
                && (lastPlayedAtUtc is null
                    || candidate > lastPlayedAtUtc))
            {
                lastPlayedAtUtc = candidate;
            }
        }

        string? launcherPath = _launcherPaths.FirstOrDefault(
            IsSafeExistingExecutable);
        if (launcherPath is null
            && lastPlayedAtUtc is null)
        {
            return null;
        }

        return new(
            launcherPath,
            lastPlayedAtUtc,
            TotalPlaytimeMinutes: null,
            HeroArtworkPath: null);
    }

    private static async ValueTask<DateTimeOffset?> ReadLatestLastPlayedAsync(
        string settingsPath,
        string catalogItemId,
        CancellationToken cancellationToken)
    {
        if (!IsReadableSettingsFile(settingsPath))
        {
            return null;
        }

        try
        {
            await using FileStream stream = new(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumSettingsBytes)
            {
                return null;
            }

            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            DateTimeOffset? latest = null;
            while (await reader.ReadLineAsync(cancellationToken)
                       .ConfigureAwait(false) is string line)
            {
                if (!line.StartsWith(
                        LastPlayedPrefix,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryParseLastPlayed(
                        line.AsSpan(LastPlayedPrefix.Length),
                        catalogItemId,
                        out DateTimeOffset timestamp)
                    && (latest is null || timestamp > latest))
                {
                    latest = timestamp;
                }
            }

            return latest;
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool TryParseLastPlayed(
        ReadOnlySpan<char> value,
        string catalogItemId,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        int commaIndex = value.LastIndexOf(',');
        if (commaIndex <= 0 || commaIndex >= value.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> identity = value[..commaIndex];
        int firstSeparator = identity.IndexOf(':');
        if (firstSeparator < 0)
        {
            return false;
        }

        ReadOnlySpan<char> afterNamespace = identity[(firstSeparator + 1)..];
        int secondSeparator = afterNamespace.IndexOf(':');
        if (secondSeparator <= 0
            || !afterNamespace[..secondSeparator].Equals(
                catalogItemId.AsSpan(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            value[(commaIndex + 1)..],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces
                | DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool IsReadableSettingsFile(string path)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists
                && file.Length is > 0 and <= MaximumSettingsBytes;
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

    private static bool IsSafeExistingExecutable(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path)
                && !path.StartsWith(@"\\", StringComparison.Ordinal)
                && Path.GetExtension(path).Equals(
                    ".exe",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(path);
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

    private static string[] NormalizeCandidatePaths(
        IEnumerable<string> paths) =>
        paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => Path.IsPathFullyQualified(path))
            .Where(path => !path.StartsWith(@"\\", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> FindDefaultSettingsPaths()
    {
        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            yield return Path.Combine(
                localApplicationData,
                "EpicGamesLauncher",
                "Saved",
                "Config",
                "WindowsEditor",
                "GameUserSettings.ini");
        }
    }

    private static IEnumerable<string> FindDefaultLauncherPaths()
    {
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.ProgramFiles,
                 })
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            yield return Path.Combine(
                root,
                "Epic Games",
                "Launcher",
                "Portal",
                "Binaries",
                "Win64",
                "EpicGamesLauncher.exe");
            yield return Path.Combine(
                root,
                "Epic Games",
                "Launcher",
                "Portal",
                "Binaries",
                "Win32",
                "EpicGamesLauncher.exe");
        }
    }
}

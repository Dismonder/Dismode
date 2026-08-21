using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace GameShift.Windows.Profiles;

public static partial class InstalledGameDiscoveryService
{
    private const int MaximumManifestBytes = 2 * 1024 * 1024;
    private const int MaximumExecutablesPerGame = 128;
    private const int MaximumXboxInstallations = 256;
    private const int MaximumScanDepth = 3;
    private static readonly TimeSpan SteamScanBudget =
        TimeSpan.FromSeconds(8);

    private static readonly string[] PenalizedExecutableTerms =
    [
        "anticheat",
        "benchmark",
        "bootstrap",
        "cefprocess",
        "config",
        "crash",
        "diagnostic",
        "dxsetup",
        "easyanticheat",
        "helper",
        "installer",
        "launcher",
        "prereq",
        "redist",
        "report",
        "server",
        "setup",
        "support",
        "unins",
        "uninstall",
        "update",
        "vc_redist",
        "webhelper",
    ];

    public static Task<DetectedGame[]> DiscoverAsync(
        CancellationToken cancellationToken) =>
        Task.Run(
            () => Discover(cancellationToken),
            cancellationToken);

    private static DetectedGame[] Discover(
        CancellationToken cancellationToken)
    {
        List<DetectedGame> detected = [];
        DiscoverEpic(detected, cancellationToken);
        DiscoverGog(detected, cancellationToken);
        DiscoverRoblox(detected, cancellationToken);
        DiscoverXbox(detected, cancellationToken);
        using (CancellationTokenSource steamScan =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
        {
            steamScan.CancelAfter(SteamScanBudget);
            try
            {
                DiscoverSteam(detected, steamScan.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                // Return the games already found instead of blocking startup.
                // A later manual scan can continue the Steam search.
            }
        }

        return detected
            .Where(game =>
                File.Exists(game.ExecutablePath)
                && Path.IsPathFullyQualified(game.ExecutablePath))
            .GroupBy(
                game => Path.GetFullPath(game.ExecutablePath),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(game => game.Confidence)
                .First())
            .OrderBy(
                game => game.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void DiscoverXbox(
        List<DetectedGame> output,
        CancellationToken cancellationToken)
    {
        int inspectedInstallations = 0;
        foreach (string xboxRoot in FindXboxRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> installationDirectories;
            try
            {
                installationDirectories = Directory
                    .EnumerateDirectories(
                        xboxRoot,
                        "*",
                        SearchOption.TopDirectoryOnly)
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

            foreach (string installationDirectory
                         in installationDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inspectedInstallations > MaximumXboxInstallations)
                {
                    return;
                }

                string directoryName = Path.GetFileName(
                    installationDirectory);
                if (directoryName.Equals(
                        "GameSave",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string contentDirectory = Path.Combine(
                    installationDirectory,
                    "Content");
                string manifestPath = Path.Combine(
                    contentDirectory,
                    "MicrosoftGame.Config");
                if (!File.Exists(manifestPath))
                {
                    contentDirectory = installationDirectory;
                    manifestPath = Path.Combine(
                        contentDirectory,
                        "MicrosoftGame.Config");
                }

                DetectedGame? game = TryReadXboxGame(
                    manifestPath,
                    contentDirectory,
                    directoryName);
                if (game is not null)
                {
                    output.Add(game);
                }
            }
        }
    }

    private static DetectedGame? TryReadXboxGame(
        string manifestPath,
        string contentDirectory,
        string fallbackDisplayName)
    {
        try
        {
            FileInfo manifest = new(manifestPath);
            if (!manifest.Exists
                || manifest.Length <= 0
                || manifest.Length > MaximumManifestBytes)
            {
                return null;
            }

            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumManifestBytes,
            };
            using FileStream stream = File.OpenRead(manifestPath);
            using XmlReader reader = XmlReader.Create(stream, settings);
            XDocument document = XDocument.Load(
                reader,
                LoadOptions.None);
            XElement? root = document.Root;
            if (root is null
                || !root.Name.LocalName.Equals(
                    "Game",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            XElement? identity = root
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals(
                        "Identity",
                        StringComparison.OrdinalIgnoreCase));
            XElement? shellVisuals = root
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName.Equals(
                        "ShellVisuals",
                        StringComparison.OrdinalIgnoreCase));
            string displayName = ReadUsableDisplayName(
                    shellVisuals?.Attribute("DefaultDisplayName")?.Value)
                ?? fallbackDisplayName;
            string externalId =
                identity?.Attribute("Name")?.Value
                ?? fallbackDisplayName;

            (string Path, long Score)[] candidates = root
                .Descendants()
                .Where(element =>
                    element.Name.LocalName.Equals(
                        "Executable",
                        StringComparison.OrdinalIgnoreCase))
                .Where(element =>
                    !bool.TryParse(
                        element.Attribute("IsDevOnly")?.Value,
                        out bool isDevOnly)
                    || !isDevOnly)
                .Select(element =>
                {
                    string? name = element.Attribute("Name")?.Value;
                    if (string.IsNullOrWhiteSpace(name)
                        || Path.IsPathFullyQualified(name))
                    {
                        return ((string Path, long Score)?)null;
                    }

                    string executable = Path.GetFullPath(
                        Path.Combine(contentDirectory, name));
                    if (!File.Exists(executable)
                        || !IsWithinDirectory(
                            executable,
                            contentDirectory)
                        || !Path.GetExtension(executable).Equals(
                            ".exe",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    string targetDeviceFamily =
                        element.Attribute("TargetDeviceFamily")?.Value
                        ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(targetDeviceFamily)
                        && !targetDeviceFamily.Contains(
                            "Desktop",
                            StringComparison.OrdinalIgnoreCase)
                        && !targetDeviceFamily.Equals(
                            "PC",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    long score = element.Attribute("Id")?.Value.Equals(
                            "Game",
                            StringComparison.OrdinalIgnoreCase) == true
                        ? 10_000
                        : 0;
                    score += Math.Min(
                        new FileInfo(executable).Length
                            / (1024 * 1024),
                        5_000);
                    if (PenalizedExecutableTerms.Any(term =>
                        executable.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        score -= 20_000;
                    }

                    return (executable, score);
                })
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!.Value)
                .Where(candidate => candidate.Score >= 0)
                .OrderByDescending(candidate => candidate.Score)
                .ToArray();

            return candidates.Length == 0
                ? null
                : new(
                    "Xbox",
                    externalId,
                    displayName,
                    candidates[0].Path,
                    LaunchArguments: [],
                    Confidence: 100);
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or XmlException
                or ArgumentException)
        {
            return null;
        }
    }

    private static HashSet<string> FindXboxRoots()
    {
        HashSet<string> roots =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                AddExistingDirectory(
                    roots,
                    Path.Combine(
                        drive.RootDirectory.FullName,
                        "XboxGames"));
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
            {
            }
        }

        return roots;
    }

    private static string? ReadUsableDisplayName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith(
            "ms-resource:",
            StringComparison.OrdinalIgnoreCase)
            ? value
            : null;

    private static void DiscoverSteam(
        List<DetectedGame> output,
        CancellationToken cancellationToken)
    {
        HashSet<string> steamRoots =
            FindSteamRoots();
        HashSet<string> libraries =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (string steamRoot in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExistingDirectory(libraries, steamRoot);
            string libraryFolders = Path.Combine(
                steamRoot,
                "steamapps",
                "libraryfolders.vdf");
            foreach (string path in ReadVdfValues(
                         libraryFolders,
                         "path"))
            {
                AddExistingDirectory(
                    libraries,
                    path.Replace(@"\\", @"\"));
            }
        }

        foreach (string library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string steamApps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            foreach (string manifestPath in Directory.EnumerateFiles(
                         steamApps,
                         "appmanifest_*.acf",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Dictionary<string, string> values =
                    ReadFlatVdf(manifestPath);
                if (!values.TryGetValue("appid", out string? appId)
                    || !values.TryGetValue(
                        "name",
                        out string? displayName)
                    || !values.TryGetValue(
                        "installdir",
                        out string? installDirectoryName))
                {
                    continue;
                }

                string installDirectory = Path.Combine(
                    steamApps,
                    "common",
                    installDirectoryName);
                string? executable = FindLikelyGameExecutable(
                    installDirectory,
                    displayName,
                    installDirectoryName,
                    cancellationToken);
                if (executable is null)
                {
                    continue;
                }

                output.Add(
                    new(
                        "Steam",
                        appId,
                        displayName,
                        executable,
                        LaunchArguments: [],
                        Confidence: 80));
            }
        }
    }

    private static void DiscoverEpic(
        List<DetectedGame> output,
        CancellationToken cancellationToken)
    {
        string manifestsDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Epic",
            "EpicGamesLauncher",
            "Data",
            "Manifests");
        if (!Directory.Exists(manifestsDirectory))
        {
            return;
        }

        foreach (string manifestPath in Directory.EnumerateFiles(
                     manifestsDirectory,
                     "*.item",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new FileInfo(manifestPath).Length > MaximumManifestBytes)
            {
                continue;
            }

            try
            {
                using FileStream stream = File.OpenRead(manifestPath);
                using JsonDocument document = JsonDocument.Parse(stream);
                JsonElement root = document.RootElement;
                string? displayName = ReadJsonString(
                    root,
                    "DisplayName");
                string? installLocation = ReadJsonString(
                    root,
                    "InstallLocation");
                string? launchExecutable = ReadJsonString(
                    root,
                    "LaunchExecutable");
                string externalId =
                    ReadJsonString(root, "CatalogItemId")
                    ?? ReadJsonString(root, "AppName")
                    ?? Path.GetFileNameWithoutExtension(manifestPath);
                if (string.IsNullOrWhiteSpace(displayName)
                    || string.IsNullOrWhiteSpace(installLocation)
                    || string.IsNullOrWhiteSpace(launchExecutable))
                {
                    continue;
                }

                string executable = Path.GetFullPath(
                    Path.Combine(
                        installLocation,
                        launchExecutable));
                if (File.Exists(executable)
                    && IsWithinDirectory(
                        executable,
                        installLocation))
                {
                    output.Add(
                        new(
                            "Epic Games",
                            externalId,
                            displayName,
                            executable,
                            LaunchArguments: [],
                            Confidence: 100));
                }
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or ArgumentException)
            {
            }
        }
    }

    private static void DiscoverGog(
        List<DetectedGame> output,
        CancellationToken cancellationToken)
    {
        foreach (RegistryView view in new[]
                 {
                     RegistryView.Registry64,
                     RegistryView.Registry32,
                 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using RegistryKey baseKey =
                    RegistryKey.OpenBaseKey(
                        RegistryHive.LocalMachine,
                        view);
                using RegistryKey? gamesKey =
                    baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (gamesKey is null)
                {
                    continue;
                }

                foreach (string gameId in gamesKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using RegistryKey? gameKey =
                        gamesKey.OpenSubKey(gameId);
                    string? displayName =
                        gameKey?.GetValue("gameName") as string;
                    string? executable =
                        gameKey?.GetValue("exe") as string;
                    string? installDirectory =
                        gameKey?.GetValue("path") as string;
                    if (string.IsNullOrWhiteSpace(displayName)
                        || string.IsNullOrWhiteSpace(executable))
                    {
                        continue;
                    }

                    string fullExecutable = Path.IsPathFullyQualified(
                        executable)
                        ? Path.GetFullPath(executable)
                        : Path.GetFullPath(
                            Path.Combine(
                                installDirectory ?? string.Empty,
                                executable));
                    if (File.Exists(fullExecutable))
                    {
                        output.Add(
                            new(
                                "GOG",
                                gameId,
                                displayName,
                                fullExecutable,
                                LaunchArguments: [],
                                Confidence: 100));
                    }
                }
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException
                    or ArgumentException)
            {
            }
        }
    }

    private static void DiscoverRoblox(
        List<DetectedGame> output,
        CancellationToken cancellationToken)
    {
        string versionsDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return;
        }

        string? executable = Directory
            .EnumerateFiles(
                versionsDirectory,
                "RobloxPlayerBeta.exe",
                SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => file.FullName)
            .FirstOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
        if (executable is not null)
        {
            output.Add(
                new(
                    "Roblox",
                    "RobloxPlayer",
                    "Roblox",
                    executable,
                    LaunchArguments: [],
                    Confidence: 100));
        }
    }

    private static HashSet<string> FindSteamRoots()
    {
        HashSet<string> roots =
            new(StringComparer.OrdinalIgnoreCase);
        AddRegistryPath(
            roots,
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamPath");
        AddRegistryPath(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"SOFTWARE\Valve\Steam",
            "InstallPath");
        AddRegistryPath(
            roots,
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            @"SOFTWARE\Valve\Steam",
            "InstallPath");
        AddExistingDirectory(
            roots,
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                "Steam"));
        return roots;
    }

    private static void AddRegistryPath(
        ISet<string> output,
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(
                hive,
                view);
            using RegistryKey? key = baseKey.OpenSubKey(keyPath);
            AddExistingDirectory(
                output,
                key?.GetValue(valueName) as string);
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or ArgumentException)
        {
        }
    }

    private static string? FindLikelyGameExecutable(
        string installDirectory,
        string displayName,
        string installDirectoryName,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(installDirectory))
        {
            return null;
        }

        string normalizedDisplayName = NormalizeName(displayName);
        string normalizedInstallName =
            NormalizeName(installDirectoryName);
        List<(string Path, long Score)> candidates = [];
        Queue<(string Directory, int Depth)> directories = [];
        directories.Enqueue((installDirectory, 0));

        while (directories.Count > 0
            && candidates.Count < MaximumExecutablesPerGame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string currentDirectory, int depth) =
                directories.Dequeue();
            try
            {
                foreach (string executable in Directory.EnumerateFiles(
                             currentDirectory,
                             "*.exe",
                             SearchOption.TopDirectoryOnly))
                {
                    string fileName = Path.GetFileNameWithoutExtension(
                        executable);
                    string normalizedFileName = NormalizeName(fileName);
                    long score = new FileInfo(executable).Length
                        / (1024 * 1024);
                    if (normalizedFileName == normalizedDisplayName
                        || normalizedFileName == normalizedInstallName)
                    {
                        score += 10_000;
                    }
                    else if (normalizedDisplayName.Contains(
                            normalizedFileName,
                            StringComparison.Ordinal)
                        || normalizedFileName.Contains(
                            normalizedInstallName,
                            StringComparison.Ordinal))
                    {
                        score += 2_000;
                    }

                    if (PenalizedExecutableTerms.Any(term =>
                        fileName.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase)
                        || executable.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        score -= 20_000;
                    }

                    candidates.Add((executable, score));
                    if (candidates.Count
                        >= MaximumExecutablesPerGame)
                    {
                        break;
                    }
                }

                if (depth >= MaximumScanDepth)
                {
                    continue;
                }

                foreach (string child in Directory.EnumerateDirectories(
                             currentDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!PenalizedExecutableTerms.Any(term =>
                        child.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        directories.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException)
            {
            }
        }

        return candidates
            .Where(candidate => candidate.Score >= 0)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private static Dictionary<string, string> ReadFlatVdf(
        string path)
    {
        string text = ReadSmallTextFile(path);
        return VdfPairRegex()
            .Matches(text)
            .Select(match =>
                new KeyValuePair<string, string>(
                    match.Groups["key"].Value,
                    UnescapeVdf(match.Groups["value"].Value)))
            .GroupBy(
                pair => pair.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ReadVdfValues(
        string path,
        string key)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        string text = ReadSmallTextFile(path);
        return VdfPairRegex()
            .Matches(text)
            .Where(match => string.Equals(
                match.Groups["key"].Value,
                key,
                StringComparison.OrdinalIgnoreCase))
            .Select(match =>
                UnescapeVdf(match.Groups["value"].Value))
            .ToArray();
    }

    private static string ReadSmallTextFile(string path)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Length > MaximumManifestBytes)
        {
            return string.Empty;
        }

        return File.ReadAllText(path);
    }

    private static string? ReadJsonString(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(
            propertyName,
            out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static string NormalizeName(string value) =>
        new(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

    private static void AddExistingDirectory(
        ISet<string> output,
        string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)
            && Directory.Exists(directory))
        {
            output.Add(Path.GetFullPath(directory));
        }
    }

    private static bool IsWithinDirectory(
        string candidatePath,
        string directoryPath)
    {
        string relative = Path.GetRelativePath(
            Path.GetFullPath(directoryPath),
            Path.GetFullPath(candidatePath));
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static string UnescapeVdf(string value) =>
        value
            .Replace(@"\\", @"\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    [GeneratedRegex(
        "\"(?<key>(?:\\\\.|[^\"])*)\"\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex VdfPairRegex();
}

namespace Dismode.Windows.Processes;

public static class ProcessClassificationService
{
    private static readonly HashSet<string> RequiredProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationFrameHost",
            "audiodg",
            "conhost",
            "csrss",
            "ctfmon",
            "dwm",
            "explorer",
            "fontdrvhost",
            "Idle",
            "lsass",
            "OpenConsole",
            "Registry",
            "RuntimeBroker",
            "SearchHost",
            "services",
            "ShellExperienceHost",
            "sihost",
            "smss",
            "StartMenuExperienceHost",
            "svchost",
            "System",
            "taskhostw",
            "TextInputHost",
            "wininit",
            "winlogon",
            "WUDFHost",
        };

    private static readonly HashSet<string> GameInfrastructureNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Agent",
            "Battle.net",
            "BEService",
            "BEService_x64",
            "Discord",
            "DiscordCanary",
            "DiscordPTB",
            "EAAntiCheat.GameServiceLauncher",
            "EABackgroundService",
            "EADesktop",
            "EasyAntiCheat",
            "EasyAntiCheat_EOS",
            "EpicGamesLauncher",
            "GalaxyClient",
            "GalaxyClientService",
            "GameBar",
            "GameBarFTServer",
            "NVIDIA Share",
            "obs32",
            "obs64",
            "PnkBstrA",
            "PnkBstrB",
            "RiotClientServices",
            "RiotClientUx",
            "RockstarService",
            "Rockstar Games Launcher",
            "SocialClubHelper",
            "steam",
            "steamwebhelper",
            "UbisoftConnect",
            "upc",
            "vgc",
            "vgtray",
        };

    public static ProcessClassification Classify(
        ProcessSnapshot process,
        int currentSessionId,
        string? gameExecutablePath = null) =>
        Classify(
            process.Name,
            process.ExecutablePath,
            process.SessionId,
            currentSessionId,
            gameExecutablePath);

    public static ProcessClassification Classify(
        string processName,
        string? executablePath,
        int processSessionId,
        int currentSessionId,
        string? gameExecutablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        // Takze wydania sprzed zmiany nazwy: do czasu instalacji nowej wersji
        // ich usluga i tray moga dzialac obok Dismode.
        if (processName.StartsWith(
                "Dismode",
                StringComparison.OrdinalIgnoreCase)
            || processName.StartsWith(
                "GameShift",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProcessSafetyClassification.RequiredSystem,
                "Składnik Dismode odpowiedzialny za sesję lub recovery.",
                "Pozostaw");
        }

        if (RequiredProcessNames.Contains(processName))
        {
            return new(
                ProcessSafetyClassification.RequiredSystem,
                "Proces wymagany przez pulpit, logowanie, dźwięk, wejście "
                + "lub podstawowe działanie Windows.",
                "Pozostaw");
        }

        if (GameInfrastructureNames.Contains(processName)
            || processName.Contains(
                "anticheat",
                StringComparison.OrdinalIgnoreCase)
            || processName.Contains(
                "anti-cheat",
                StringComparison.OrdinalIgnoreCase)
            || processName.Contains(
                "faceit",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProcessSafetyClassification.GameInfrastructure,
                "Launcher, anti-cheat albo infrastruktura, której gra może "
                + "wymagać do startu lub działania.",
                "Pozostaw");
        }

        if (!string.IsNullOrWhiteSpace(executablePath)
            && !string.IsNullOrWhiteSpace(gameExecutablePath))
        {
            string processFullPath = Path.GetFullPath(executablePath);
            string gameFullPath = Path.GetFullPath(gameExecutablePath);
            ProcessClassification? asGame =
                ClassifyAgainstGame(processFullPath, gameFullPath);
            if (asGame is null)
            {
                // Sciezka procesu przychodzi z jadra z rozwiazanymi
                // junctionami, sciezka z biblioteki nie: przeniesiona
                // biblioteka Steam zostawia te dwie postacie rozne.
                // Otwarcie pliku gry kosztuje, wiec placimy za nie dopiero
                // wtedy, gdy porownanie tekstu niczego nie rozstrzygnelo.
                ResolvedGamePaths resolved = ResolveGamePaths(gameFullPath);
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        resolved.ExecutablePath,
                        gameFullPath))
                {
                    asGame = ClassifyAgainstGame(
                        processFullPath,
                        resolved.ExecutablePath);
                }

                // Junction na samym katalogu gry: za nim znacznik biblioteki
                // (steamapps\common) juz nie wystepuje, wiec root wyprowadzony
                // z rozwiazanej sciezki EXE bylby za waski na helpery obok
                // katalogu z EXE. Root z biblioteki, rozwiazany osobno, je
                // obejmuje.
                asGame ??= ClassifyWithinInstallation(
                    processFullPath,
                    resolved.InstallationRoot);
            }

            if (asGame is not null)
            {
                return asGame;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath))
        {
            return new(
                ProcessSafetyClassification.Unsupported,
                "Brak bezpiecznie rozpoznanej ścieżki pliku wykonywalnego.",
                "Pozostaw");
        }

        string fullPath = Path.GetFullPath(executablePath);
        string windowsDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsWithinDirectory(fullPath, windowsDirectory))
        {
            return new(
                ProcessSafetyClassification.RequiredSystem,
                "Plik wykonywalny należy do Windows.",
                "Pozostaw");
        }

        if (fullPath.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps"
                    + $"{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProcessSafetyClassification.Unsupported,
                "Aplikacja pakietowa nie ma pewnego bezpośredniego restartu.",
                "Pozostaw");
        }

        if (processSessionId != currentSessionId)
        {
            return new(
                ProcessSafetyClassification.RequiredSystem,
                "Proces działa poza bieżącą sesją użytkownika.",
                "Pozostaw");
        }

        return new(
            ProcessSafetyClassification.OptionalUser,
            "Zwykły proces bieżącego użytkownika, niezależny od znanej "
            + "infrastruktury gry.",
            "Decyzja użytkownika");
    }

    /// <summary>
    /// The verdict for a process that turns out to belong to the selected
    /// game, or null when its path says nothing about it. Both paths have to
    /// be in the same form — both as written down, or both resolved — or the
    /// comparison is between a junction and what lies behind it.
    /// </summary>
    private static ProcessClassification? ClassifyAgainstGame(
        string processFullPath,
        string gameFullPath)
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(
                processFullPath,
                gameFullPath))
        {
            return new(
                ProcessSafetyClassification.GameInfrastructure,
                "Główny proces wybranej gry.",
                "Monitoruj; priorytet zmieniaj wyłącznie ręcznie");
        }

        return ClassifyWithinInstallation(
            processFullPath,
            GetGameInstallationRoot(gameFullPath));
    }

    private static ProcessClassification? ClassifyWithinInstallation(
        string processFullPath,
        string installationRoot) =>
        IsWithinDirectory(processFullPath, installationRoot)
            ? new(
                ProcessSafetyClassification.GameInfrastructure,
                "Proces pochodzi z katalogu instalacyjnego wybranej gry "
                + "i może być jej składnikiem pomocniczym.",
                "Pozostaw podczas gry")
            : null;

    private sealed record ResolvedGamePaths(
        string ExecutablePath,
        string InstallationRoot,
        DateTimeOffset ResolvedAtUtc);

    private static readonly TimeSpan ResolvedGamePathsLifetime =
        TimeSpan.FromSeconds(30);

    private const int ResolvedGamePathsCapacity = 16;

    private static readonly Lock ResolvedGamePathsLock = new();

    private static readonly Dictionary<string, ResolvedGamePaths>
        ResolvedGamePathsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The library path of the game and its installation root with junctions
    /// resolved. Resolving opens the file, and classification walks hundreds
    /// of processes per scan, so one game is resolved once per half-minute
    /// rather than once per process; on a disconnected share a single open
    /// can wait for seconds, and without this every process would.
    /// </summary>
    private static ResolvedGamePaths ResolveGamePaths(string gameFullPath)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (ResolvedGamePathsLock)
        {
            if (ResolvedGamePathsCache.TryGetValue(
                    gameFullPath,
                    out ResolvedGamePaths? cached)
                && now - cached.ResolvedAtUtc < ResolvedGamePathsLifetime)
            {
                return cached;
            }
        }

        ResolvedGamePaths resolved = new(
            ExecutablePathIdentity.ResolveFinalPathOrSelf(gameFullPath),
            ExecutablePathIdentity.ResolveFinalPathOrSelf(
                GetGameInstallationRoot(gameFullPath)),
            now);
        lock (ResolvedGamePathsLock)
        {
            if (ResolvedGamePathsCache.Count >= ResolvedGamePathsCapacity
                && !ResolvedGamePathsCache.ContainsKey(gameFullPath))
            {
                ResolvedGamePathsCache.Clear();
            }

            ResolvedGamePathsCache[gameFullPath] = resolved;
        }

        return resolved;
    }

    private static bool IsWithinDirectory(
        string candidatePath,
        string directoryPath)
    {
        string relative = Path.GetRelativePath(
            directoryPath,
            candidatePath);
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static string GetGameInstallationRoot(
        string gameExecutablePath)
    {
        string fullPath = Path.GetFullPath(gameExecutablePath);
        string separator = Path.DirectorySeparatorChar.ToString();
        string[] installationMarkers =
        [
            $"{separator}steamapps{separator}common{separator}",
            $"{separator}Epic Games{separator}",
            $"{separator}GOG Galaxy{separator}Games{separator}",
            $"{separator}Roblox{separator}Versions{separator}",
            $"{separator}XboxGames{separator}",
        ];
        foreach (string marker in installationMarkers)
        {
            int markerIndex = fullPath.IndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            int gameDirectoryStart = markerIndex + marker.Length;
            int gameDirectoryEnd = fullPath.IndexOf(
                Path.DirectorySeparatorChar,
                gameDirectoryStart);
            if (gameDirectoryEnd > gameDirectoryStart)
            {
                return fullPath[..gameDirectoryEnd];
            }
        }

        return Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The game executable directory is invalid.",
                nameof(gameExecutablePath));
    }
}

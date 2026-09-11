using System.ComponentModel;
using System.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Security;
using GameShift.Windows.Sessions;

namespace GameShift.Windows.Processes;

public sealed class BackgroundApplicationGuard
{
    private static readonly HashSet<string> ProtectedProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ApplicationFrameHost",
            "audiodg",
            "BEService",
            "BEService_x64",
            "conhost",
            "csrss",
            "ctfmon",
            "dwm",
            "EAAntiCheat.GameServiceLauncher",
            "EasyAntiCheat",
            "EasyAntiCheat_EOS",
            "explorer",
            "fontdrvhost",
            "GameShift",
            "GameShift.Launcher",
            "GameShift.SessionHost",
            "GameShift.SystemAgent",
            "GameShift.UI",
            "Idle",
            "lsass",
            "OpenConsole",
            "PnkBstrA",
            "PnkBstrB",
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
            "vgc",
            "vgtray",
            "wininit",
            "winlogon",
            "WUDFHost",
        };

    private static readonly HashSet<string> ProtectedLauncherNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Agent",
            "Battle.net",
            "Discord",
            "DiscordCanary",
            "DiscordPTB",
            "EABackgroundService",
            "EADesktop",
            "EpicGamesLauncher",
            "GalaxyClient",
            "GalaxyClientService",
            "GameBar",
            "GameBarFTServer",
            "NVIDIA Share",
            "obs32",
            "obs64",
            "RiotClientServices",
            "RiotClientUx",
            "RockstarService",
            "Rockstar Games Launcher",
            "SocialClubHelper",
            "steam",
            "steamwebhelper",
            "UbisoftConnect",
            "upc",
        };

    private readonly IProcessIdentityProvider _identityProvider;

    public BackgroundApplicationGuard(
        IProcessIdentityProvider? identityProvider = null)
    {
        _identityProvider =
            identityProvider ?? new ProcessIdentityProvider();
    }

    public async ValueTask<PlannedBackgroundApplication> ApproveAsync(
        BackgroundApplicationSelection selection,
        string gameExecutablePath,
        CancellationToken cancellationToken)
    {
        if (selection.ProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "Identyfikator procesu musi być dodatni.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutablePath);
        if (!Enum.IsDefined(selection.ActionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "Nieobsługiwany sposób optymalizacji procesu.");
        }

        try
        {
            using Process process =
                Process.GetProcessById(selection.ProcessId);
            process.Refresh();

            DateTimeOffset startedAtUtc = new(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero);
            if (startedAtUtc.ToUnixTimeMilliseconds()
                != selection.StartedAtUtc
                    .ToUniversalTime()
                    .ToUnixTimeMilliseconds())
            {
                throw new InvalidOperationException(
                    "Wybrany proces zakończył się lub jego PID został użyty ponownie. "
                    + "Uruchom analizę jeszcze raz.");
            }

            string processName = process.ProcessName;
            EnsureNameAllowed(processName);

            if (process.SessionId != Process.GetCurrentProcess().SessionId)
            {
                throw new InvalidOperationException(
                    "Można optymalizować wyłącznie aplikacje z bieżącej sesji Windows.");
            }

            if (selection.ActionMode
                    == BackgroundProcessActionMode.CloseAndRestore
                && !ProcessWindowHelper.HasInteractiveWindow(process))
            {
                throw new InvalidOperationException(
                    $"Aplikacja „{processName}” nie ma okna, które można bezpiecznie zamknąć.");
            }

            ProcessIdentity identity =
                await _identityProvider
                    .TryCaptureAsync(process, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Nie można bezpiecznie potwierdzić tożsamości „{processName}”.");

            string currentSid = CurrentWindowsIdentity.GetUserSid().Value;
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    identity.UserSid,
                    currentSid))
            {
                throw new InvalidOperationException(
                    "Nie można zamknąć aplikacji należącej do innego użytkownika.");
            }

            string executablePath = Path.GetFullPath(
                identity.ExecutablePath);
            ProcessClassification classification =
                ProcessClassificationService.Classify(
                    processName,
                    executablePath,
                    process.SessionId,
                    Process.GetCurrentProcess().SessionId,
                    gameExecutablePath);
            if (classification.Kind
                != ProcessSafetyClassification.OptionalUser)
            {
                throw new InvalidOperationException(
                    classification.Reason);
            }

            EnsurePathAllowed(executablePath, gameExecutablePath);
            ApplicationRestartDescriptor? restartDescriptor = null;
            if (selection.ActionMode
                == BackgroundProcessActionMode.CloseAndRestore)
            {
                EnsureSingleInteractiveInstance(identity);
                string workingDirectory =
                    Path.GetDirectoryName(executablePath)
                    ?? throw new InvalidOperationException(
                        "Nie można ustalić katalogu aplikacji.");
                restartDescriptor = new(
                    executablePath,
                    workingDirectory,
                    arguments: [],
                    ApplicationRestartability.Restartable);
            }
            else if (process.PriorityClass is not (
                ProcessPriorityClass.Normal
                or ProcessPriorityClass.BelowNormal))
            {
                throw new InvalidOperationException(
                    $"Proces „{processName}” ma priorytet, którego GameShift "
                    + "nie będzie automatycznie obniżać.");
            }

            ActionId? ecoQosActionId = null;
            IdempotencyKey? ecoQosIdempotencyKey = null;
            ActionId? affinityActionId = null;
            IdempotencyKey? affinityIdempotencyKey = null;
            ActionId? ioPriorityActionId = null;
            IdempotencyKey? ioPriorityIdempotencyKey = null;
            ActionId? memoryPriorityActionId = null;
            IdempotencyKey? memoryPriorityIdempotencyKey = null;
            if (selection.ActionMode
                is BackgroundProcessActionMode.LowerPriorityAndEcoQos
                    or BackgroundProcessActionMode.RestrainBackground)
            {
                _ = ProcessPowerThrottlingController.Read(process);
                ecoQosActionId = ActionId.Create();
                ecoQosIdempotencyKey = IdempotencyKey.Create();
            }

            if (selection.ActionMode
                == BackgroundProcessActionMode.RestrainBackground)
            {
                // Pelny pakiet dla tla, po jednej dzwigni na zasob:
                // rdzenie (twarda maska cwiartki — jedyna dzwignia CPU,
                // ktora w pomiarze ruszyla czas klatki: p99 lepsze o 60,7%
                // na Valheim, gdzie samo obnizenie priorytetu nie dawalo
                // nic), pamiec (priorytet pamieci, zeby pod presja
                // wylatywaly strony tla, nie gry) i dysk (priorytet
                // wejscia-wyjscia, zeby kolejka odczytow nalezala do gry).
                // Identyfikatory powstaja tu, bo kazda z tych zmian
                // przezywa smierc GameShifta i musi zostac co odwrocic —
                // tak samo jak przy EcoQoS. Czy maszyna kwalifikuje sie do
                // maski, rozstrzyga sie dopiero przy nakladaniu; jesli nie,
                // akcja jest pomijana, a odtwarzanie nie ma czego cofac.
                affinityActionId = ActionId.Create();
                affinityIdempotencyKey = IdempotencyKey.Create();
                ioPriorityActionId = ActionId.Create();
                ioPriorityIdempotencyKey = IdempotencyKey.Create();
                memoryPriorityActionId = ActionId.Create();
                memoryPriorityIdempotencyKey = IdempotencyKey.Create();
            }

            return new(
                ActionId.Create(),
                IdempotencyKey.Create(),
                processName,
                identity,
                selection.ActionMode,
                ecoQosActionId,
                ecoQosIdempotencyKey,
                affinityActionId,
                affinityIdempotencyKey,
                ioPriorityActionId,
                ioPriorityIdempotencyKey,
                memoryPriorityActionId,
                memoryPriorityIdempotencyKey,
                restartDescriptor,
                Math.Max(0, process.WorkingSet64));
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or IOException
                or Win32Exception)
        {
            throw new InvalidOperationException(
                $"Nie można dodać wybranej aplikacji do bezpiecznego planu: "
                + exception.Message,
                exception);
        }
    }

    /// <summary>
    /// True for processes GameShift must leave alone. The shell, the input
    /// stack and the compositor are here because touching them is felt
    /// immediately by the person at the keyboard: trimming their working set
    /// costs far more in stutter and slow redraws than it ever returns in
    /// memory. Anti-cheat and GameShift's own services are here for the
    /// obvious reasons.
    /// </summary>
    public static bool IsProtectedProcessName(string? processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && (ProtectedProcessNames.Contains(processName)
            || ProtectedLauncherNames.Contains(processName)
            || IsMeasurementComponent(processName)
            || LooksLikeAntiCheat(processName));

    /// <summary>
    /// Anti-cheat by fragment of name, for the components the fixed lists
    /// do not spell out. The reactive loop and the descendant sweep both
    /// ask this question, and until now only the approval path did — so a
    /// service named after its vendor rather than its product could be
    /// caught by the loop, given the corner mask and a VeryLow I/O
    /// priority, and start failing integrity checks it has to finish on
    /// time. Substrings are deliberately broad; a false positive here costs
    /// one process left alone, a miss costs a kicked player.
    /// </summary>
    private static readonly string[] AntiCheatFragments =
    [
        "anticheat",
        "anti-cheat",
        "faceit",
        "gameguard",
        "gamemon",
        "xhunter",
        "xigncode",
        "nprotect",
        "vanguard",
        "ricochet",
        "javelin",
        "mhyprot",
        "hyperprotect",
        "aceanticheat",
        "ace-base",
        "acetray",
        "wellbia",
        "battleye",
        "easyanticheat",
    ];

    private static bool LooksLikeAntiCheat(string processName) =>
        AntiCheatFragments.Any(fragment =>
            processName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// GameShift's own frame-time capture. Matched by prefix because the
    /// executable carries its version in the file name, and pinning the exact
    /// name here would quietly stop protecting it after the next component
    /// update.
    /// <para>
    /// Throttling it does not merely lose data. It consumes ETW events in real
    /// time, so starving it makes it drop events, and dropped events read back
    /// as long frames — GameShift would report a stutter it caused itself, and
    /// any measurement taken while restraint is active would be worthless.
    /// </para>
    /// </summary>
    private static bool IsMeasurementComponent(string processName) =>
        processName.StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase);

    private static void EnsureNameAllowed(string processName)
    {
        if (ProtectedProcessNames.Contains(processName))
        {
            throw new InvalidOperationException(
                $"Proces „{processName}” jest chronionym elementem Windows lub GameShift.");
        }

        if (ProtectedLauncherNames.Contains(processName))
        {
            throw new InvalidOperationException(
                $"Launcher „{processName}” pozostaje uruchomiony, ponieważ gra może go wymagać.");
        }

        if (LooksLikeAntiCheat(processName))
        {
            throw new InvalidOperationException(
                $"Proces „{processName}” wygląda na składnik anti-cheat i nie może być zmieniany.");
        }
    }

    private static void EnsurePathAllowed(
        string executablePath,
        string gameExecutablePath)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(executablePath),
                ".exe"))
        {
            throw new InvalidOperationException(
                "Wybrana aplikacja nie jest bezpośrednim plikiem EXE.");
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(
                executablePath,
                Path.GetFullPath(gameExecutablePath)))
        {
            throw new InvalidOperationException(
                "Proces gry nie może być aplikacją przeznaczoną do zamknięcia.");
        }

        string windowsDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsWithinDirectory(executablePath, windowsDirectory))
        {
            throw new InvalidOperationException(
                "GameShift nie zamyka plików wykonywalnych należących do Windows.");
        }

        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Aplikacje pakietowe Windows nie mają bezpiecznego, bezpośredniego restartu.");
        }
    }

    private static void EnsureSingleInteractiveInstance(
        ProcessIdentity identity)
    {
        int equivalentCount = 0;
        string processName = Path.GetFileNameWithoutExtension(
            identity.ExecutablePath);
        foreach (Process candidate in Process.GetProcessesByName(processName))
        {
            using (candidate)
            {
                try
                {
                    string? candidatePath =
                        candidate.MainModule?.FileName;
                    if (candidate.SessionId == identity.SessionId
                        && StringComparer.OrdinalIgnoreCase.Equals(
                            candidatePath,
                            identity.ExecutablePath)
                        && ProcessWindowHelper.HasInteractiveWindow(candidate))
                    {
                        equivalentCount++;
                    }
                }
                catch (Exception exception) when (
                    exception is
                        InvalidOperationException
                        or NotSupportedException
                        or UnauthorizedAccessException
                        or Win32Exception)
                {
                }
            }
        }

        if (equivalentCount != 1)
        {
            throw new InvalidOperationException(
                "Bezpieczne zamknięcie wymaga dokładnie jednego głównego "
                + "procesu okna tej aplikacji.");
        }
    }

    private static bool IsWithinDirectory(
        string candidatePath,
        string directoryPath)
    {
        string relative = Path.GetRelativePath(
            directoryPath,
            candidatePath);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative);
    }
}

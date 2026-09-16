using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GameShift.Contracts.Protocol;
using GameShift.Core.Activation;
using GameShift.Windows.Processes;
using GameShift.Windows.Security;

namespace GameShift.UI.Services;

/// <summary>
/// Bramka przed pierwszym oknem. Dwie rzeczy, bez ktorych GameShift nie ma
/// sensu: jeden egzemplarz na sesje uzytkownika (drugi oddaje zadanie
/// pierwszemu i konczy prace) oraz dzialajacy, uprawniony GameShift.SessionHost
/// z tego samego katalogu. Bez hosta interfejs bylby atrapa — nic nie
/// optymalizuje — wiec zamiast pokazywac atrape, prosi o uprawnienia,
/// a po odmowie mowi dlaczego i konczy prace.
/// </summary>
internal static class StartupGate
{
    private const string SessionHostExecutableName = "GameShift.SessionHost.exe";
    private const string SessionHostProcessName = "GameShift.SessionHost";
    private const string UiProcessName = "GameShift.UI";
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipeWait = TimeSpan.FromSeconds(8);

    // Trzymany do konca procesu; zwolnienie oddaloby nazwe drugiemu
    // egzemplarzowi, poki ten jeszcze dziala.
    private static SingleInstanceLock? _instanceLock;

    /// <summary>
    /// Null, gdy okno ma powstac; inaczej kod wyjscia, z ktorym proces ma
    /// sie zakonczyc (komunikat dla uzytkownika juz pokazany).
    /// </summary>
    public static int? Run(GameShiftLaunchOptions launchOptions)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        string userSid = CurrentWindowsIdentity.GetUserSid().Value;

        _instanceLock = SingleInstanceLock.TryAcquire(
            SingleInstanceLock.BuildName("UI", userSid));
        if (_instanceLock is null)
        {
            HandOverToRunningInstance(userSid, launchOptions);
            return 0;
        }

        string baseDirectory = TrimSeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        string hostPath = Path.Combine(baseDirectory, SessionHostExecutableName);
        if (!File.Exists(hostPath))
        {
            ShowError(
                "Brakuje składnika GameShift.SessionHost.exe obok "
                + "GameShift.UI.exe. Zainstaluj GameShift ponownie.");
            return 2;
        }

        string? foreignHostDirectory = FindSessionHostDirectory(
            exceptDirectory: baseDirectory);
        if (foreignHostDirectory is not null)
        {
            ShowError(
                "Działa już GameShift z innej lokalizacji:"
                + Environment.NewLine
                + foreignHostDirectory
                + Environment.NewLine
                + Environment.NewLine
                + "Dwa egzemplarze psułyby sobie nawzajem sesje. Zamknij "
                + "tamten GameShift (ikona w zasobniku → Wyłącz GameShift) "
                + "i uruchom ponownie.");
            return 3;
        }

        if (FindSessionHostDirectory(onlyDirectory: baseDirectory) is null)
        {
            try
            {
                StartElevated(hostPath, baseDirectory);
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode == ErrorCancelled)
            {
                ShowError(
                    "GameShift potrzebuje uprawnień administratora, żeby "
                    + "uruchomić usługę sesji (GameShift.SessionHost). Bez "
                    + "niej nie optymalizuje gier, więc nie uruchamia się "
                    + "w ogóle." + Environment.NewLine + Environment.NewLine
                    + "Uruchom GameShift ponownie i zatwierdź monit UAC.");
                return 4;
            }
            catch (Exception exception) when (
                exception is
                    Win32Exception
                    or InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException)
            {
                ShowError(
                    "Nie udało się uruchomić GameShift.SessionHost: "
                    + exception.Message);
                return 5;
            }

            // Host rejestruje rure w ulamku sekundy, ale okno pokazane
            // wczesniej zaczeloby od komunikatu „usluga niedostepna".
            WaitForPipe(PipeNames.ForUser(userSid), PipeWait);
        }

        return null;
    }

    private static void HandOverToRunningInstance(
        string userSid,
        GameShiftLaunchOptions launchOptions)
    {
        if (launchOptions.GameExecutablePath is null
            && launchOptions.StartInBackground)
        {
            // Start „w tle" bez gry niczego od dzialajacego okna nie chce;
            // wyciaganie go na wierzch byloby odwrotnoscia prosby.
            return;
        }

        UiActivationRequest request = launchOptions.GameExecutablePath is string game
            ? new(
                UiActivationProtocol.CurrentSchemaVersion,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                game,
                launchOptions.StartInBackground)
            : new(
                UiActivationProtocol.CurrentSchemaVersion,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "GameShift.UI.exe"),
                KeepWindowHidden: false,
                ShowOnly: true);
        if (UiActivationClient.TrySend(
                userSid,
                request,
                HandOverTimeout,
                out string? failure))
        {
            return;
        }

        if (launchOptions.GameExecutablePath is not null)
        {
            ShowError(
                "GameShift już działa, ale nie przyjął żądania uruchomienia "
                + "gry: " + failure);
            return;
        }

        // Rura aktywacji milczy (np. okno w trakcie startu): ostatnia deska
        // ratunku to zwykle pokazanie glownego okna tamtego procesu.
        foreach (Process process in Process.GetProcessesByName(UiProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId
                        || process.SessionId != CurrentSessionId)
                    {
                        continue;
                    }

                    nint window = process.MainWindowHandle;
                    if (window != nint.Zero)
                    {
                        _ = ShowWindow(window, ShowWindowMaximized);
                        _ = SetForegroundWindow(window);
                        return;
                    }
                }
                catch (Exception exception) when (
                    exception is
                        InvalidOperationException
                        or Win32Exception
                        or NotSupportedException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Katalog dzialajacego hosta w tej sesji: pierwszy spoza
    /// <paramref name="exceptDirectory"/> albo, gdy podano
    /// <paramref name="onlyDirectory"/>, wylacznie z tego katalogu.
    /// </summary>
    private static string? FindSessionHostDirectory(
        string? exceptDirectory = null,
        string? onlyDirectory = null)
    {
        foreach (Process process in Process.GetProcessesByName(SessionHostProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != CurrentSessionId)
                    {
                        continue;
                    }

                    string? path = ProcessImagePath.TryRead(process);
                    string? directory = path is null
                        ? null
                        : Path.GetDirectoryName(Path.GetFullPath(path));
                    if (directory is null)
                    {
                        continue;
                    }

                    directory = TrimSeparator(directory);
                    bool sameAsExcept = exceptDirectory is not null
                        && StringComparer.OrdinalIgnoreCase.Equals(
                            directory,
                            exceptDirectory);
                    bool sameAsOnly = onlyDirectory is not null
                        && StringComparer.OrdinalIgnoreCase.Equals(
                            directory,
                            onlyDirectory);
                    if (onlyDirectory is not null ? sameAsOnly : !sameAsExcept)
                    {
                        return directory;
                    }
                }
                catch (Exception exception) when (
                    exception is
                        InvalidOperationException
                        or Win32Exception
                        or NotSupportedException)
                {
                }
            }
        }

        return null;
    }

    private static void StartElevated(string hostPath, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = hostPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using Process started = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows nie uruchomił GameShift.SessionHost.");
    }

    private static void WaitForPipe(string pipeName, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        string expected = @"\\.\pipe\" + pipeName;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (Directory.GetFiles(@"\\.\pipe\").Any(pipe =>
                        StringComparer.OrdinalIgnoreCase.Equals(pipe, expected)))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }

            Thread.Sleep(250);
        }
    }

    private static string TrimSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void ShowError(string message) =>
        _ = MessageBox(
            nint.Zero,
            message,
            "GameShift",
            MessageBoxIconError | MessageBoxSetForeground);

    private static int CurrentSessionId
    {
        get
        {
            using Process current = Process.GetCurrentProcess();
            return current.SessionId;
        }
    }

    private const int ShowWindowMaximized = 3;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxSetForeground = 0x00010000;

    // DllImport, nie LibraryImport: generator tego drugiego wymaga
    // niebezpiecznego kodu, ktorego projekt interfejsu nie wlacza.
#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        nint windowHandle,
        string text,
        string caption,
        uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
#pragma warning restore SYSLIB1054
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dismode.Contracts.Protocol;
using Dismode.Core.Activation;
using Dismode.Data.Storage;
using Dismode.Windows.Processes;
using Dismode.Windows.Security;

namespace Dismode.UI.Services;

/// <summary>
/// Bramka przed pierwszym oknem. Dwie rzeczy, bez ktorych Dismode nie ma
/// sensu: jeden egzemplarz na sesje uzytkownika (drugi oddaje zadanie
/// pierwszemu i konczy prace) oraz dzialajacy, uprawniony Dismode.SessionHost
/// z tego samego katalogu. Bez hosta interfejs bylby atrapa — nic nie
/// optymalizuje — wiec zamiast pokazywac atrape, prosi o uprawnienia,
/// a po odmowie mowi dlaczego i konczy prace.
/// </summary>
internal static class StartupGate
{
    private const string SessionHostExecutableName = "Dismode.SessionHost.exe";
    private const string SessionHostProcessName = "Dismode.SessionHost";
    private const string UiProcessName = "Dismode.UI";
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PipeWait = TimeSpan.FromSeconds(8);

    // Trzymany do konca procesu; zwolnienie oddaloby nazwe drugiemu
    // egzemplarzowi, poki ten jeszcze dziala.
    private static SingleInstanceLock? _instanceLock;

    /// <summary>
    /// Null, gdy w tej sesji nie dziala zaden skladnik wydania pod stara
    /// nazwa (GameShift); inaczej kod wyjscia (komunikat juz pokazany).
    /// Tamten program ma te same gry i ten sam katalog danych, a jego mutexy
    /// nosza stara nazwe, wiec <see cref="Run"/> by go nie zauwazyl.
    /// </summary>
    public static int? RefuseWhileLegacyProductRuns()
    {
        if (LegacyProductProcesses.FindRunningInCurrentSession()
            is not string legacyExecutable)
        {
            return null;
        }

        StartupDecision decision =
            StartupPolicy.LegacyProductRunning(legacyExecutable);
        ShowError(decision.Message ?? string.Empty);
        return decision.ExitCode;
    }

    /// <summary>
    /// Przenosi dane z wydan pod stara nazwa. Null, gdy okno moze powstac;
    /// kod wyjscia, gdy baza albo dziennik poprzedniej wersji zostaly poza
    /// zasiegiem — okno z pusta biblioteka udawaloby wtedy program.
    /// </summary>
    public static int? MigrateLegacyData(Action<string> reportProblem)
    {
        ArgumentNullException.ThrowIfNull(reportProblem);
        LegacyStorageMigrationResult migration =
            LegacyStorageMigration.MigrateUserData();
        foreach (string problem in migration.Problems)
        {
            reportProblem(problem);
        }

        if (!migration.BlocksStartup)
        {
            return null;
        }

        StartupDecision decision =
            StartupPolicy.LegacyDataUnavailable(migration.Problems);
        ShowError(decision.Message ?? string.Empty);
        return decision.ExitCode;
    }

    /// <summary>
    /// Null, gdy okno ma powstac; inaczej kod wyjscia, z ktorym proces ma
    /// sie zakonczyc (komunikat dla uzytkownika juz pokazany).
    /// </summary>
    public static int? Run(DismodeLaunchOptions launchOptions)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        string userSid = CurrentWindowsIdentity.GetUserSid().Value;
        string baseDirectory = TrimSeparator(
            Path.GetFullPath(AppContext.BaseDirectory));
        string hostPath = Path.Combine(baseDirectory, SessionHostExecutableName);

        _instanceLock = SingleInstanceLock.TryAcquire(
            SingleInstanceLock.BuildName("UI", userSid));
        StartupDecision decision = StartupPolicy.Decide(new(
            InstanceLockAcquired: _instanceLock is not null,
            HostExecutableExists: File.Exists(hostPath),
            ForeignHostDirectory: _instanceLock is null
                ? null
                : FindSessionHostDirectory(exceptDirectory: baseDirectory),
            OwnHostRunning: _instanceLock is not null
                && FindSessionHostDirectory(onlyDirectory: baseDirectory)
                    is not null));

        switch (decision.Kind)
        {
            case StartupDecisionKind.HandOver:
                HandOverToRunningInstance(userSid, launchOptions);
                return decision.ExitCode;
            case StartupDecisionKind.Continue:
                return null;
            case StartupDecisionKind.StartHost:
                break;
            default:
                ShowError(decision.Message ?? string.Empty);
                return decision.ExitCode;
        }

        Process host;
        try
        {
            host = StartElevated(hostPath, baseDirectory);
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == ErrorCancelled)
        {
            StartupDecision declined = StartupPolicy.ElevationDeclined();
            ShowError(declined.Message ?? string.Empty);
            return declined.ExitCode;
        }
        catch (Exception exception) when (
            exception is
                Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            StartupDecision failed = StartupPolicy.HostStartFailed(
                exception.Message);
            ShowError(failed.Message ?? string.Empty);
            return failed.ExitCode;
        }

        // Host rejestruje rure w ulamku sekundy, ale okno pokazane
        // wczesniej zaczeloby od komunikatu „usluga niedostepna".
        using (host)
        {
            try
            {
                if (!WaitForPipe(PipeNames.ForUser(userSid), host, PipeWait)
                    && host.HasExited
                    && host.ExitCode != 0)
                {
                    // Host sprawdzil to samo co my, tylko pozniej: GameShift
                    // mogl wystartowac w miedzyczasie albo migracja utknela.
                    StartupDecision refused =
                        StartupPolicy.HostRefused(host.ExitCode);
                    ShowError(refused.Message ?? string.Empty);
                    return refused.ExitCode;
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException)
            {
                // Uchwyt bez prawa odczytu stanu: okno rozstrzygnie po rurze.
            }
        }

        return null;
    }

    private static void HandOverToRunningInstance(
        string userSid,
        DismodeLaunchOptions launchOptions)
    {
        if (StartupPolicy.HandsOverSilently(launchOptions))
        {
            return;
        }

        UiActivationRequest request = StartupPolicy.BuildHandOverRequest(
            launchOptions,
            Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Dismode.UI.exe"),
            DateTimeOffset.UtcNow);
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
                "Dismode już działa, ale nie przyjął żądania uruchomienia "
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

    private static Process StartElevated(string hostPath, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = hostPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows nie uruchomił Dismode.SessionHost.");
    }

    /// <summary>
    /// True once the host's pipe exists; false when the host quit first or
    /// the wait ran out.
    /// </summary>
    private static bool WaitForPipe(
        string pipeName,
        Process host,
        TimeSpan timeout)
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
                    return true;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }

            if (host.HasExited)
            {
                return false;
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static string TrimSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void ShowError(string message) =>
        _ = MessageBox(
            nint.Zero,
            message,
            "Dismode",
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

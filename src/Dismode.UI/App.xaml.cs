using Dismode.Core.Activation;
using Dismode.UI.Services;
using Microsoft.UI.Xaml;

namespace Dismode.UI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            string[] commandLineArguments = Environment.GetCommandLineArgs()
                .Skip(1)
                .ToArray();
            DismodeLaunchOptions launchOptions;
            try
            {
                launchOptions = DismodeLaunchOptions.Parse(
                    commandLineArguments);
            }
            catch (ArgumentException)
            {
                launchOptions = new(
                    StartInBackground: false,
                    GameExecutablePath: null);
            }

            // Poprzednia wersja (GameShift) w tej sesji: te same gry i, do
            // pierwszego startu, ten sam katalog danych. Sprawdzane przed
            // migracja, zeby nie przenosic danych spod dzialajacego programu.
            if (StartupGate.RefuseWhileLegacyProductRuns() is int legacyExitCode)
            {
                Environment.Exit(legacyExitCode);
                return;
            }

            // Przed pierwszym odczytem bazy: inaczej UI zalozyloby pusta
            // baze obok niezmigrowanych danych z wydan GameShift.
            if (StartupGate.MigrateLegacyData(problem =>
                    WriteStartupFailure(
                        "LegacyStorageMigration",
                        new IOException(problem)))
                is int migrationExitCode)
            {
                Environment.Exit(migrationExitCode);
                return;
            }

            // Jeden egzemplarz na sesje i dzialajacy, uprawniony host —
            // inaczej okno bez zadnej optymalizacji, a takie udaje program.
            if (StartupGate.Run(launchOptions) is int exitCode)
            {
                Environment.Exit(exitCode);
                return;
            }

            MainWindow window = new(launchOptions);
            _window = window;
            window.Activate();
            if (launchOptions.StartInBackground)
            {
                window.HideToTray();
            }
        }
        catch (Exception exception)
        {
            WriteStartupFailure("OnLaunched", exception);
            throw;
        }
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        WriteStartupFailure("UnhandledException", args.Exception);

    private static void WriteStartupFailure(
        string stage,
        Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Dismode");
            Directory.CreateDirectory(directory);
            string entry = $"[{DateTimeOffset.UtcNow:O}] {stage}"
                + Environment.NewLine
                + exception
                + Environment.NewLine
                + Environment.NewLine;
            File.AppendAllText(
                Path.Combine(directory, "ui-startup-errors.log"),
                entry);
        }
        catch (Exception loggingException) when (
            loggingException is IOException
                or UnauthorizedAccessException)
        {
        }
    }
}

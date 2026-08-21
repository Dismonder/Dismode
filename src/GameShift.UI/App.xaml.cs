using GameShift.Core.Activation;
using Microsoft.UI.Xaml;

namespace GameShift.UI;

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
            GameShiftLaunchOptions launchOptions;
            try
            {
                launchOptions = GameShiftLaunchOptions.Parse(
                    commandLineArguments);
            }
            catch (ArgumentException)
            {
                launchOptions = new(
                    StartInBackground: false,
                    GameExecutablePath: null);
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
                "GameShift");
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

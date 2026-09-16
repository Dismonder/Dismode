namespace GameShift.Core.Activation;

public enum StartupDecisionKind
{
    /// <summary>Another GameShift owns this session: hand over and exit.</summary>
    HandOver = 1,

    /// <summary>GameShift.SessionHost.exe is not next to the window.</summary>
    HostMissing = 2,

    /// <summary>A SessionHost from a different directory is already running.</summary>
    ForeignHostRunning = 3,

    /// <summary>No host from this directory yet: start it with elevation.</summary>
    StartHost = 4,

    /// <summary>Everything in place: show the window.</summary>
    Continue = 5,
}

/// <summary>
/// What the window found before deciding whether to appear.
/// </summary>
/// <param name="InstanceLockAcquired">Whether this process holds the per-session lock.</param>
/// <param name="HostExecutableExists">Whether GameShift.SessionHost.exe lies in the window's directory.</param>
/// <param name="ForeignHostDirectory">Directory of a running SessionHost that is not ours, or null.</param>
/// <param name="OwnHostRunning">Whether a SessionHost from our directory is running.</param>
public sealed record StartupObservation(
    bool InstanceLockAcquired,
    bool HostExecutableExists,
    string? ForeignHostDirectory,
    bool OwnHostRunning);

/// <param name="ExitCode">Process exit code when the window must not appear.</param>
/// <param name="Message">Text for the user, or null when nothing needs saying.</param>
public sealed record StartupDecision(
    StartupDecisionKind Kind,
    int ExitCode,
    string? Message);

/// <summary>
/// The rules behind the first window, kept free of processes and UAC so
/// they can be tested: one GameShift per session, and no window without an
/// elevated SessionHost from the same directory, because such a window
/// optimizes nothing and only looks like the program.
/// </summary>
public static class StartupPolicy
{
    public const int ExitHandedOver = 0;
    public const int ExitHostMissing = 2;
    public const int ExitForeignHost = 3;
    public const int ExitElevationDeclined = 4;
    public const int ExitHostStartFailed = 5;

    public static StartupDecision Decide(StartupObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!observation.InstanceLockAcquired)
        {
            return new(StartupDecisionKind.HandOver, ExitHandedOver, null);
        }

        if (!observation.HostExecutableExists)
        {
            return new(
                StartupDecisionKind.HostMissing,
                ExitHostMissing,
                "Brakuje składnika GameShift.SessionHost.exe obok "
                + "GameShift.UI.exe. Zainstaluj GameShift ponownie.");
        }

        if (observation.ForeignHostDirectory is string foreign)
        {
            return new(
                StartupDecisionKind.ForeignHostRunning,
                ExitForeignHost,
                "Działa już GameShift z innej lokalizacji:"
                + Environment.NewLine
                + foreign
                + Environment.NewLine
                + Environment.NewLine
                + "Dwa egzemplarze psułyby sobie nawzajem sesje. Zamknij "
                + "tamten GameShift (ikona w zasobniku → Wyłącz GameShift) "
                + "i uruchom ponownie.");
        }

        return observation.OwnHostRunning
            ? new(StartupDecisionKind.Continue, 0, null)
            : new(StartupDecisionKind.StartHost, 0, null);
    }

    public static StartupDecision ElevationDeclined() =>
        new(
            StartupDecisionKind.StartHost,
            ExitElevationDeclined,
            "GameShift potrzebuje uprawnień administratora, żeby uruchomić "
            + "usługę sesji (GameShift.SessionHost). Bez niej nie "
            + "optymalizuje gier, więc nie uruchamia się w ogóle."
            + Environment.NewLine
            + Environment.NewLine
            + "Uruchom GameShift ponownie i zatwierdź monit UAC.");

    public static StartupDecision HostStartFailed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(
            StartupDecisionKind.StartHost,
            ExitHostStartFailed,
            "Nie udało się uruchomić GameShift.SessionHost: " + reason);
    }

    /// <summary>
    /// A background start without a game asks nothing of the running window;
    /// pulling it to the front would be the opposite of the request.
    /// </summary>
    public static bool HandsOverSilently(GameShiftLaunchOptions launchOptions)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        return launchOptions.GameExecutablePath is null
            && launchOptions.StartInBackground;
    }

    /// <summary>
    /// The request a losing instance sends to the running window: launch
    /// the requested game, or just come to the front. The sender's own
    /// executable stands in for the required path in the latter case.
    /// </summary>
    public static UiActivationRequest BuildHandOverRequest(
        GameShiftLaunchOptions launchOptions,
        string ownExecutablePath,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownExecutablePath);
        return launchOptions.GameExecutablePath is string game
            ? new(
                UiActivationProtocol.CurrentSchemaVersion,
                Guid.NewGuid(),
                nowUtc,
                game,
                launchOptions.StartInBackground)
            : new(
                UiActivationProtocol.CurrentSchemaVersion,
                Guid.NewGuid(),
                nowUtc,
                ownExecutablePath,
                KeepWindowHidden: false,
                ShowOnly: true);
    }
}

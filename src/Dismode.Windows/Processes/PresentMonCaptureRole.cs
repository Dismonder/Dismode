namespace Dismode.Windows.Processes;

/// <summary>
/// Who is measuring. Each role gets its own ETW session, because the overlay
/// (in the UI process) and the game session (in the session host) run as the
/// same user at the same time, and PresentMon starts with
/// <c>--stop_existing_session</c>: under a shared name every start stopped the
/// other's capture and neither ever delivered a frame.
/// </summary>
public enum PresentMonCaptureRole
{
    /// <summary>The game session's own measurement and its history.</summary>
    Session = 0,

    /// <summary>The on-screen overlay following the foreground window.</summary>
    Overlay = 1,
}

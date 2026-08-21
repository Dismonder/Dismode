using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Profiles;

public sealed record LaunchedGameProcess(
    ProcessIdentity Identity,
    bool WasAlreadyRunning = false);

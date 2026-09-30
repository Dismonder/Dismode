using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Profiles;

public sealed record LaunchedGameProcess(
    ProcessIdentity Identity,
    bool WasAlreadyRunning = false);

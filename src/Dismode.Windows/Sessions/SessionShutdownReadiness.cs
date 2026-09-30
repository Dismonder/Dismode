namespace Dismode.Windows.Sessions;

public sealed record SessionShutdownReadiness(
    bool CanShutdown,
    bool HasActiveSession,
    bool HasPreparedPlan,
    string Message);

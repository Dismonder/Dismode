namespace Dismode.Windows.Processes;

public enum FrameRateStatus
{
    MissingComponent = 1,
    InvalidComponent = 2,
    Starting = 3,
    WaitingForGame = 4,
    Measuring = 5,
    AccessDenied = 6,
    Failed = 7,
    Disabled = 8,
}

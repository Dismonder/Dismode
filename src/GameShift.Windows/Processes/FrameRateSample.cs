namespace GameShift.Windows.Processes;

public sealed record FrameRateSample(
    FrameRateStatus Status,
    double? FramesPerSecond,
    double? FrameTimeMilliseconds,
    int? ProcessId,
    string Message)
{
    public static FrameRateSample MissingComponent(string message) =>
        new(
            FrameRateStatus.MissingComponent,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            ProcessId: null,
            message);

    public static FrameRateSample InvalidComponent(string message) =>
        new(
            FrameRateStatus.InvalidComponent,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            ProcessId: null,
            message);

    public static FrameRateSample Starting(int processId) =>
        new(
            FrameRateStatus.Starting,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            processId,
            $"PresentMon uruchamia pomiar dla procesu gry PID {processId}.");

    public static FrameRateSample Restarting(int processId) =>
        new(
            FrameRateStatus.Starting,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            processId,
            "PresentMon ponawia czysty pomiar po braku pierwszych klatek "
            + $"procesu gry PID {processId}.");

    public static FrameRateSample WaitingForGame(int? processId = null) =>
        new(
            FrameRateStatus.WaitingForGame,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            processId,
            processId is int targetProcessId
                ? "Oczekiwanie na prawdziwe zdarzenia renderowania procesu "
                    + $"gry PID {targetProcessId}."
                : "Oczekiwanie na prawdziwe zdarzenia renderowania aktywnej gry.");

    public static FrameRateSample Failed(string message) =>
        new(
            FrameRateStatus.Failed,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            ProcessId: null,
            message);

    public static FrameRateSample Disabled() =>
        new(
            FrameRateStatus.Disabled,
            FramesPerSecond: null,
            FrameTimeMilliseconds: null,
            ProcessId: null,
            "Pomiar FPS jest wyłączony w ustawieniach GameShift.");
}

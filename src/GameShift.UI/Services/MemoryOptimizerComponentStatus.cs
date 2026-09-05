namespace GameShift.UI.Services;

public enum MemoryOptimizerComponentState
{
    NotInstalled = 0,
    Running = 1,
    Paused = 2,
    ServiceStopped = 3,
    InterfaceStopped = 4,
}

public sealed record MemoryOptimizerComponentSnapshot(
    MemoryOptimizerComponentState State,
    string DisplayState,
    string Details,
    bool CanOpen);

public static class MemoryOptimizerComponentPresentation
{
    public static MemoryOptimizerComponentSnapshot CreateInstalledSnapshot(
        bool serviceRunning,
        bool trayRunning,
        bool paused)
    {
        if (!serviceRunning)
        {
            return new(
                MemoryOptimizerComponentState.ServiceStopped,
                "Usługa zatrzymana",
                "Składnik jest zainstalowany, ale jego niezależna usługa nie działa.",
                true);
        }

        if (!trayRunning)
        {
            return new(
                MemoryOptimizerComponentState.InterfaceStopped,
                "Interfejs zamknięty",
                "Usługa optymalizacji działa w tle. Otwórz interfejs, aby zmienić ustawienia.",
                true);
        }

        return paused
            ? new(
                MemoryOptimizerComponentState.Paused,
                "Wstrzymany",
                "Usługa działa, a automatyczna optymalizacja jest wstrzymana.",
                true)
            : new(
                MemoryOptimizerComponentState.Running,
                "Działa",
                "Niezależna usługa, automat i interfejs Memory Optimizer działają.",
                true);
    }

    public static string? GetLaunchFailure(
        bool interfaceWasRunning,
        bool launchedProcessExited,
        int exitCode)
    {
        if (interfaceWasRunning || !launchedProcessExited)
        {
            return null;
        }

        return exitCode == 0
            ? "Interfejs Memory Optimizer zakończył działanie podczas uruchamiania."
            : $"Interfejs Memory Optimizer zakończył działanie podczas uruchamiania " +
                $"(kod 0x{unchecked((uint)exitCode):X8}).";
    }
}

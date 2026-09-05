namespace GameShift.UI.Services;

public enum SystemOptimizerComponentState
{
    NotInstalled = 0,
    ServiceUnavailable = 1,
    Ready = 2,
    ReadOnly = 3,
    ActiveGameProfile = 4,
    ActiveExperiment = 5,
    RecoveryRequired = 6,
}

public sealed record SystemOptimizerComponentSnapshot(
    SystemOptimizerComponentState State,
    string DisplayState,
    string Details,
    bool CanOpen);

public static class SystemOptimizerComponentPresentation
{
    public static SystemOptimizerComponentSnapshot CreateSnapshot(
        bool executableExists,
        bool serviceReachable,
        bool isReadOnly,
        bool isRecoveryClean,
        string? activeGameProfileId,
        string? activeExperimentId,
        string? serviceMessage)
    {
        if (!executableExists)
        {
            return new(
                SystemOptimizerComponentState.NotInstalled,
                "Niezainstalowany",
                "Składnik System Optimizer nie jest zainstalowany.",
                CanOpen: false);
        }

        if (!serviceReachable)
        {
            return new(
                SystemOptimizerComponentState.ServiceUnavailable,
                "Usługa niedostępna",
                "Interfejs jest zainstalowany, ale usługa GameShiftSystemAgent nie odpowiada.",
                CanOpen: true);
        }

        if (!string.IsNullOrWhiteSpace(activeExperimentId))
        {
            return new(
                SystemOptimizerComponentState.ActiveExperiment,
                "Trwa eksperyment A/B",
                $"Aktywny eksperyment: {activeExperimentId}",
                CanOpen: true);
        }

        if (!string.IsNullOrWhiteSpace(activeGameProfileId))
        {
            return new(
                SystemOptimizerComponentState.ActiveGameProfile,
                "Aktywny profil gry",
                $"Profil {activeGameProfileId} jest stosowany do bieżącej sesji.",
                CanOpen: true);
        }

        if (!isRecoveryClean)
        {
            return new(
                SystemOptimizerComponentState.RecoveryRequired,
                "Wymaga przywrócenia",
                string.IsNullOrWhiteSpace(serviceMessage)
                    ? "Journal zawiera niezakończoną zmianę systemową."
                    : serviceMessage,
                CanOpen: true);
        }

        if (isReadOnly)
        {
            return new(
                SystemOptimizerComponentState.ReadOnly,
                "Tylko odczyt",
                string.IsNullOrWhiteSpace(serviceMessage)
                    ? "Klient nie ma zaufanego podpisu wymaganego do mutacji."
                    : serviceMessage,
                CanOpen: true);
        }

        return new(
            SystemOptimizerComponentState.Ready,
            "Gotowy",
            string.IsNullOrWhiteSpace(serviceMessage)
                ? "Usługa jest bezczynna i czeka na świadomą zgodę."
                : serviceMessage,
            CanOpen: true);
    }
}

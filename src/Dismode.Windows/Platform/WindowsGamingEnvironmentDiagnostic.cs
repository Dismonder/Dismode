using Microsoft.Win32;

namespace Dismode.Windows.Platform;

public static class WindowsGamingEnvironmentDiagnostic
{
    public static WindowsGamingDiagnosticReport Evaluate()
    {
        bool? hagsEnabled = CheckHagsEnabled();
        bool gameModeEnabled = CheckGameModeEnabled();
        bool backgroundRecordingEnabled = CheckBackgroundRecordingEnabled();

        List<WindowsGamingRecommendation> recommendations = [];

        if (hagsEnabled == false)
        {
            recommendations.Add(
                new(
                    "Planowanie procesora GPU z akceleracją sprzętową (HAGS) jest wyłączone.",
                    "Włącz HAGS w: Ustawienia Windows -> System -> Ekran -> Grafika -> Zaawansowane ustawienia grafiki, aby zmniejszyć opóźnienia GPU.",
                    DiagnosticSeverity.Warning));
        }

        if (!gameModeEnabled)
        {
            recommendations.Add(
                new(
                    "Tryb gry Windows (Game Mode) jest wyłączony.",
                    "Zaleca się włączenie Trybu gry w Ustawieniach Windows dla optymalnego szeregowania wątków gier.",
                    DiagnosticSeverity.Warning));
        }

        if (backgroundRecordingEnabled)
        {
            recommendations.Add(
                new(
                    "Nagrywanie w tle Xbox Game DVR jest aktywne.",
                    "Jeśli nie nagrywasz powtórek, wyłączenie Game DVR w Ustawieniach Windows zwolni zasoby kodera karty graficznej.",
                    DiagnosticSeverity.Info));
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(
                new(
                    "Środowisko Windows jest optymalnie skonfigurowane do gier.",
                    "Wszystkie kluczowe mechanizmy akceleracji i szeregowania grafiki są aktywne.",
                    DiagnosticSeverity.Success));
        }

        return new(
            HagsEnabled: hagsEnabled,
            GameModeEnabled: gameModeEnabled,
            BackgroundRecordingEnabled: backgroundRecordingEnabled,
            Recommendations: recommendations);
    }

    private static bool? CheckHagsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key?.GetValue("HwSchMode") is int mode)
            {
                return mode == 2;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CheckGameModeEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\GameBar");
            if (key?.GetValue("AllowAutoGameMode") is int mode)
            {
                return mode != 0;
            }

            // Domyślnie w Windows 11 Game Mode jest włączony
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool CheckBackgroundRecordingEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR");
            if (key?.GetValue("AppCaptureEnabled") is int enabled)
            {
                return enabled != 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

public sealed record WindowsGamingDiagnosticReport(
    bool? HagsEnabled,
    bool GameModeEnabled,
    bool BackgroundRecordingEnabled,
    IReadOnlyList<WindowsGamingRecommendation> Recommendations);

public sealed record WindowsGamingRecommendation(
    string Title,
    string Description,
    DiagnosticSeverity Severity);

public enum DiagnosticSeverity
{
    Success,
    Info,
    Warning,
}

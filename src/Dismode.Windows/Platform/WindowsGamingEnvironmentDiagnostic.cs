using Microsoft.Win32;

namespace Dismode.Windows.Platform;

public static class WindowsGamingEnvironmentDiagnostic
{
    public static WindowsGamingDiagnosticReport Evaluate() => Evaluate(new WindowsGamingEnvironmentRegistryReader());

    public static WindowsGamingDiagnosticReport Evaluate(IGamingEnvironmentRegistryReader registryReader)
    {
        ArgumentNullException.ThrowIfNull(registryReader);
        bool? hagsEnabled = CheckHagsEnabled(registryReader);
        bool gameModeEnabled = CheckGameModeEnabled(registryReader);
        bool? backgroundRecordingEnabled = CheckBackgroundRecordingEnabled(registryReader);

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

        if (backgroundRecordingEnabled == true)
        {
            recommendations.Add(
                new(
                    "Nagrywanie w tle Xbox Game DVR jest aktywne.",
                    "Jeśli nie nagrywasz powtórek, wyłączenie Game DVR w Ustawieniach Windows zwolni zasoby kodera karty graficznej.",
                    DiagnosticSeverity.Info));
        }

        if (backgroundRecordingEnabled is null)
        {
            recommendations.Add(
                new(
                    "Nie można ustalić stanu nagrywania w tle Xbox Game DVR.",
                    "Sprawdź ustawienia nagrywania w tle w Ustawieniach Windows oraz zasady Game DVR.",
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

    private static bool? CheckHagsEnabled(IGamingEnvironmentRegistryReader registryReader)
    {
        try
        {
            if (registryReader.ReadValue(RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int mode)
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

    private static bool CheckGameModeEnabled(IGamingEnvironmentRegistryReader registryReader)
    {
        try
        {
            if (registryReader.ReadValue(RegistryHive.CurrentUser,
                @"Software\Microsoft\GameBar", "AllowAutoGameMode") is int mode)
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

    private static bool? CheckBackgroundRecordingEnabled(IGamingEnvironmentRegistryReader registryReader)
    {
        try
        {
            // Polityka ma pierwszenstwo; zezwolenie nie oznacza wlaczonego nagrywania.
            object? policy = registryReader.ReadValue(RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
            if (policy is int policyValue && policyValue == 0)
            {
                return false;
            }

            if (policy is not null && policy is not int)
            {
                return null;
            }

            if (policy is int allowed && allowed != 1)
            {
                return null;
            }

            // AppCaptureEnabled dotyczy dostepnosci przechwytywania, nie nagrywania w tle.
            return registryReader.ReadValue(RegistryHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled") switch
            {
                int value when value == 1 => true,
                int value when value == 0 => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed record WindowsGamingDiagnosticReport(
    bool? HagsEnabled,
    bool GameModeEnabled,
    bool? BackgroundRecordingEnabled,
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

public interface IGamingEnvironmentRegistryReader
{
    object? ReadValue(RegistryHive hive, string subKey, string valueName);
}

internal sealed class WindowsGamingEnvironmentRegistryReader : IGamingEnvironmentRegistryReader
{
    public object? ReadValue(RegistryHive hive, string subKey, string valueName)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using RegistryKey? key = baseKey.OpenSubKey(subKey, writable: false);
        return key?.GetValue(valueName);
    }
}

using Dismode.Core.Policies;

namespace Dismode.Windows.Services;

public static class ServiceClassificationService
{
    private static readonly string[] GameInfrastructureTerms =
    [
        "anticheat",
        "battleye",
        "easyanticheat",
        "epic online",
        "gamingservices",
        "riot",
        "steam client",
        "xbl",
        "xbox",
    ];

    public static ServiceClassification Classify(
        ServiceSnapshot service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (ProtectedServiceCatalog.Classify(service.ServiceName) != 0)
        {
            return new(
                ServiceSafetyClassification.RequiredSystem,
                "Usługa znajduje się w twardym katalogu ochronnym Windows.",
                "Nigdy nie wyłączaj");
        }

        if (service.IsDriver)
        {
            return new(
                ServiceSafetyClassification.RequiredSystem,
                "Sterowniki i usługi typu driver są zawsze chronione.",
                "Nigdy nie wyłączaj");
        }

        string searchable = string.Join(
            ' ',
            service.ServiceName,
            service.DisplayName,
            service.BinaryPath);
        if (GameInfrastructureTerms.Any(term =>
            searchable.Contains(
                term,
                StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                ServiceSafetyClassification.GameInfrastructure,
                "Usługa wygląda na launcher, anti-cheat lub infrastrukturę "
                + "platformy gier.",
                "Pozostaw podczas gry");
        }

        if (!service.IsConfigurationComplete)
        {
            return new(
                ServiceSafetyClassification.Unsupported,
                "Windows nie udostępnił pełnej konfiguracji tej usługi. "
                + "Dismode pokazuje ją w inwentaryzacji, ale nie kwalifikuje "
                + "do zmian.",
                "Pozostaw");
        }

        if (!service.CanStop
            || service.IsSharedProcess
            || service.TriggerCount > 0
            || service.RunningDependentServices.Count > 0)
        {
            return new(
                ServiceSafetyClassification.RequiredSystem,
                "Usługa nie spełnia bramy bezpiecznego stop/restart "
                + "(shared host, trigger, zależności albo brak stop).",
                "Pozostaw");
        }

        string? executablePath = TryExtractExecutablePath(
            service.BinaryPath);
        if (executablePath is null)
        {
            return new(
                ServiceSafetyClassification.Unsupported,
                "Nie można jednoznacznie rozpoznać pliku wykonywalnego usługi.",
                "Pozostaw");
        }

        string windowsDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (IsWithinDirectory(executablePath, windowsDirectory))
        {
            return new(
                ServiceSafetyClassification.RequiredSystem,
                "Usługa jest uruchamiana z katalogu Windows i pozostaje "
                + "chroniona konserwatywnie.",
                "Pozostaw");
        }

        return new(
            ServiceSafetyClassification.OptionalThirdParty,
            "Samodzielna usługa firmy trzeciej spełniająca techniczną bramę "
            + "stop/restart. Dismode nie zatrzymuje usług produkcyjnych "
            + "w tej wersji.",
            "Tylko obserwuj");
    }

    private static string? TryExtractExecutablePath(
        string binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(
            binaryPath.Trim());
        string candidate;
        if (expanded.StartsWith('"'))
        {
            int closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                return null;
            }

            candidate = expanded[1..closingQuote];
        }
        else
        {
            int executableEnd = expanded.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            if (executableEnd < 0)
            {
                return null;
            }

            candidate = expanded[..(executableEnd + 4)];
        }

        try
        {
            return Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : null;
        }
        catch (Exception exception) when (
            exception is
                ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsWithinDirectory(
        string candidatePath,
        string directoryPath)
    {
        string relative = Path.GetRelativePath(
            directoryPath,
            candidatePath);
        return !Path.IsPathFullyQualified(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }
}

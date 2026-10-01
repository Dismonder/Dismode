using Dismode.UI.Services;

namespace Dismode.UI.ViewModels;

public sealed class ServiceClassificationListItem
{
    public ServiceClassificationListItem(
        DiscoveredServiceClientSnapshot service)
    {
        Service = service;
    }

    public DiscoveredServiceClientSnapshot Service { get; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Service.DisplayName)
            ? Service.ServiceName
            : Service.DisplayName;

    public string TechnicalName =>
        $"{Service.ServiceName} • {Service.Status} • {Service.StartType}";

    public string ClassificationLabel =>
        Service.SafetyClassification switch
        {
            "RequiredSystem" => "Chroniona przez Dismode",
            "GameInfrastructure" => "Powiązana z grą lub launcherem",
            "OptionalThirdParty" => "Opcjonalna usługa firmy trzeciej",
            _ => "Brak wystarczającej pewności",
        };

    public string Explanation =>
        $"{Service.ClassificationReason} {Service.RecommendedAction}.";
}

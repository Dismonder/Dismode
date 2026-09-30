namespace Dismode.Windows.Services;

public enum ServiceSafetyClassification
{
    RequiredSystem = 1,
    GameInfrastructure = 2,
    OptionalThirdParty = 3,
    Unsupported = 4,
}

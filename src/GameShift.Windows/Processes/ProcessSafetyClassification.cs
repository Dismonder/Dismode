namespace GameShift.Windows.Processes;

public enum ProcessSafetyClassification
{
    RequiredSystem = 1,
    GameInfrastructure = 2,
    OptionalUser = 3,
    Unsupported = 4,
}

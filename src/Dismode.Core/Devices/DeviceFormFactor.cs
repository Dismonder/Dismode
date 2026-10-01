namespace Dismode.Core.Devices;

public enum DeviceFormFactor
{
    Unknown = 0,
    Desktop = 1,
    Laptop = 2,
}

public static class DeviceFormFactorExtensions
{
    public static string ToDisplayName(this DeviceFormFactor formFactor) =>
        formFactor switch
        {
            DeviceFormFactor.Desktop => "Komputer stacjonarny (PC)",
            DeviceFormFactor.Laptop => "Laptop (komputer przenośny)",
            _ => "Nieznany typ urządzenia",
        };
}

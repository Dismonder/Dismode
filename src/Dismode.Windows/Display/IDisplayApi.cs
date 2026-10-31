namespace Dismode.Windows.Display;

public sealed record DisplayMode(string DeviceName, string DeviceId, int Width, int Height,
    int BitsPerPixel, int RefreshRate, int Orientation = 0, int PositionX = 0,
    int PositionY = 0, int DisplayFlags = 0, int FixedOutput = 0);

public interface IDisplayApi
{
    bool IsUserSession { get; }
    // Null oznacza brak widocznego okna gry, nie zgode na monitor glowny.
    string? FindGameMonitor(IReadOnlyList<int> processIds);
    DisplayMode? GetCurrentMode(string deviceName);
    IReadOnlyList<DisplayMode> GetModes(string deviceName);
    void SetMode(DisplayMode mode);
}

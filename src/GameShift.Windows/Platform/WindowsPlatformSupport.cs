using GameShift.Core.Devices;
using GameShift.Windows.Devices;

namespace GameShift.Windows.Platform;

public static class WindowsPlatformSupport
{
    public static readonly Version MinimumVersion = new(10, 0, 22631);

    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(
            MinimumVersion.Major,
            MinimumVersion.Minor,
            MinimumVersion.Build);

    public static string DescribeCurrentSystem()
    {
        DeviceFormFactor formFactor = new WindowsDeviceFormFactorDetector().Detect();
        return $"{Environment.OSVersion.VersionString}; x64 process: {Environment.Is64BitProcess}; {formFactor.ToDisplayName()}";
    }
}

using GameShift.Core.Devices;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Devices;

public sealed class WindowsDeviceFormFactorDetector : IDeviceFormFactorDetector
{
    private readonly Func<(bool Success, SystemPowerStatus Status)>? _statusProvider;

    public WindowsDeviceFormFactorDetector()
    {
    }

    internal WindowsDeviceFormFactorDetector(
        Func<(bool Success, SystemPowerStatus Status)> statusProvider)
    {
        _statusProvider = statusProvider;
    }

    public DeviceFormFactor Detect()
    {
        try
        {
            bool success;
            SystemPowerStatus status;

            if (_statusProvider is not null)
            {
                (success, status) = _statusProvider();
            }
            else
            {
                success = PowerNativeMethods.GetSystemPowerStatus(out status);
            }

            if (!success)
            {
                return DeviceFormFactor.Unknown;
            }

            // BatteryFlag:
            // 128 (0x80) = No system battery
            // 255 (0xFF) = Unknown status
            // 1 = High, 2 = Low, 4 = Critical, 8 = Charging
            if ((status.BatteryFlag & 128) != 0 || status.BatteryFlag == 128)
            {
                return DeviceFormFactor.Desktop;
            }

            if (status.BatteryFlag != 255 || status.BatteryLifePercent <= 100)
            {
                return DeviceFormFactor.Laptop;
            }

            return DeviceFormFactor.Unknown;
        }
        catch
        {
            return DeviceFormFactor.Unknown;
        }
    }
}

using GameShift.Core.Devices;
using GameShift.Windows.Devices;
using GameShift.Windows.NativeInterop;

namespace GameShift.IntegrationTests.Devices;

[TestClass]
public sealed class DeviceFormFactorDetectorTests
{
    [TestMethod]
    public void DetectsDesktopWhenNoSystemBatteryFlagIsPresent()
    {
        SystemPowerStatus status = new()
        {
            ACLineStatus = 1,
            BatteryFlag = 128, // 128 = No system battery
            BatteryLifePercent = 255,
        };

        WindowsDeviceFormFactorDetector detector = new(() => (true, status));
        DeviceFormFactor formFactor = detector.Detect();

        Assert.AreEqual(DeviceFormFactor.Desktop, formFactor);
        Assert.AreEqual("Komputer stacjonarny (PC)", formFactor.ToDisplayName());
    }

    [TestMethod]
    public void DetectsLaptopWhenBatteryIsPresent()
    {
        SystemPowerStatus status = new()
        {
            ACLineStatus = 1,
            BatteryFlag = 8, // Charging
            BatteryLifePercent = 95,
        };

        WindowsDeviceFormFactorDetector detector = new(() => (true, status));
        DeviceFormFactor formFactor = detector.Detect();

        Assert.AreEqual(DeviceFormFactor.Laptop, formFactor);
        Assert.AreEqual("Laptop (komputer przenośny)", formFactor.ToDisplayName());
    }

    [TestMethod]
    public void DetectsUnknownWhenApiFails()
    {
        WindowsDeviceFormFactorDetector detector = new(() => (false, default));
        DeviceFormFactor formFactor = detector.Detect();

        Assert.AreEqual(DeviceFormFactor.Unknown, formFactor);
        Assert.AreEqual("Nieznany typ urządzenia", formFactor.ToDisplayName());
    }

    [TestMethod]
    public void LiveSystemDetectionReturnsValidFormFactor()
    {
        WindowsDeviceFormFactorDetector detector = new();
        DeviceFormFactor formFactor = detector.Detect();

        Assert.AreNotEqual(DeviceFormFactor.Unknown, formFactor);
    }
}

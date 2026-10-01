using System.ServiceProcess;
using Dismode.Windows.Power;
using Dismode.Windows.Processes;
using Dismode.Windows.Services;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class ReadOnlyInventoryTests
{
    [TestMethod]
    public void ProcessInventoryIncludesTheCurrentProcess()
    {
        ProcessInventory inventory = new();
        int currentProcessId = Environment.ProcessId;

        IReadOnlyList<ProcessSnapshot> processes = inventory.Capture();
        ProcessSnapshot? current = processes.FirstOrDefault(
            process => process.ProcessId == currentProcessId);

        Assert.IsNotNull(current);
        Assert.IsGreaterThan(0, current.ProcessId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(current.Name));
    }

    [TestMethod]
    public void ServiceInventoryReturnsEveryEnumeratedServiceWithDistinctNames()
    {
        string[] expectedNames;
        ServiceController[] controllers = ServiceController.GetServices();
        try
        {
            expectedNames = controllers
                .Select(service => service.ServiceName)
                .ToArray();
        }
        finally
        {
            foreach (ServiceController controller in controllers)
            {
                controller.Dispose();
            }
        }

        ServiceInventory inventory = new();

        IReadOnlyList<ServiceSnapshot> services = inventory.Capture();
        int distinctNames = services
            .Select(service => service.ServiceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.IsNotEmpty(services);
        Assert.AreEqual(services.Count, distinctNames);
        Assert.IsTrue(services.All(
            service => !string.IsNullOrWhiteSpace(service.ServiceName)));
        string[] missingNames = expectedNames
            .Except(
                services.Select(service => service.ServiceName),
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.IsEmpty(
            missingNames,
            $"Inventory silently omitted: {string.Join(", ", missingNames)}");
    }

    [TestMethod]
    public async Task ServiceConfigurationReaderCapturesWithoutMutation()
    {
        IReadOnlyList<ServiceSnapshot> services =
            new ServiceInventory().Capture();
        Assert.IsNotEmpty(services);
        ServiceSnapshot service = services[0];
        WindowsServiceSnapshot detailed =
            await new WindowsServiceControlAdapter().CaptureAsync(
                service.ServiceName,
                CancellationToken.None);

        Assert.AreEqual(service.ServiceName, detailed.ServiceName);
        Assert.HasCount(64, detailed.ConfigurationFingerprint);
        Assert.IsGreaterThanOrEqualTo(0, detailed.ProcessId);
        Assert.IsGreaterThanOrEqualTo(0, detailed.TriggerCount);
    }

    [TestMethod]
    public async Task ActivePowerSchemeCanBeReadWithoutMutation()
    {
        WindowsPowerSchemeAdapter adapter = new();

        Guid activeSchemeId =
            await adapter.GetActiveSchemeAsync(CancellationToken.None);
        bool activeSchemeExists =
            await adapter.SchemeExistsAsync(
                activeSchemeId,
                CancellationToken.None);

        Assert.AreNotEqual(Guid.Empty, activeSchemeId);
        Assert.IsTrue(activeSchemeExists);
    }
}

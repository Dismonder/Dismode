using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;

namespace GameShift.Windows.Services;

public sealed record WindowsServiceSnapshot
{
    public WindowsServiceSnapshot(
        string serviceName,
        string displayName,
        ServiceControllerStatus status,
        ServiceStartMode startType,
        ServiceType serviceType,
        bool canStop,
        int processId,
        string binaryPath,
        string serviceAccount,
        bool delayedAutoStart,
        int triggerCount,
        IEnumerable<string> dependencies,
        IEnumerable<string> runningDependentServices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(runningDependentServices);

        ServiceName = serviceName.Trim();
        DisplayName = displayName?.Trim() ?? string.Empty;
        Status = status;
        StartType = startType;
        ServiceType = serviceType;
        CanStop = canStop;
        ProcessId = processId;
        BinaryPath = binaryPath?.Trim() ?? string.Empty;
        ServiceAccount = serviceAccount?.Trim() ?? string.Empty;
        DelayedAutoStart = delayedAutoStart;
        TriggerCount = triggerCount;
        Dependencies = dependencies
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RunningDependentServices = runningDependentServices
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ConfigurationFingerprint = ComputeConfigurationFingerprint(this);
    }

    public string ServiceName { get; }

    public string DisplayName { get; }

    public ServiceControllerStatus Status { get; }

    public ServiceStartMode StartType { get; }

    public ServiceType ServiceType { get; }

    public bool CanStop { get; }

    public int ProcessId { get; }

    public string BinaryPath { get; }

    public string ServiceAccount { get; }

    public bool DelayedAutoStart { get; }

    public int TriggerCount { get; }

    public IReadOnlyList<string> Dependencies { get; }

    public IReadOnlyList<string> RunningDependentServices { get; }

    public string ConfigurationFingerprint { get; }

    public bool IsDriver =>
        (ServiceType
            & (ServiceType.KernelDriver
                | ServiceType.FileSystemDriver
                | ServiceType.RecognizerDriver)) != 0;

    public bool IsSharedProcess =>
        (ServiceType & ServiceType.Win32ShareProcess) != 0;

    private static string ComputeConfigurationFingerprint(
        WindowsServiceSnapshot snapshot)
    {
        string canonical = string.Join(
            '\n',
            snapshot.ServiceName.ToUpperInvariant(),
            ((int)snapshot.StartType).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ((int)snapshot.ServiceType).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            snapshot.BinaryPath,
            snapshot.ServiceAccount,
            snapshot.DelayedAutoStart ? "1" : "0",
            snapshot.TriggerCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            string.Join('\0', snapshot.Dependencies),
            string.Join('\0', snapshot.RunningDependentServices));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

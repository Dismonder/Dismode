using System.ComponentModel;
using System.ServiceProcess;

namespace GameShift.Windows.Services;

public sealed class ServiceInventory : IServiceInventory
{
    public IReadOnlyList<ServiceSnapshot> Capture()
    {
        List<ServiceSnapshot> snapshots = [];

        foreach (ServiceController service in ServiceController.GetServices())
        {
            using (service)
            {
                ServiceSnapshot? snapshot = TryCapture(service);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.ServiceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ServiceSnapshot? TryCapture(ServiceController service)
    {
        string serviceName;
        try
        {
            serviceName = service.ServiceName;
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return null;
        }

        bool isComplete = true;
        string displayName = TryRead(
            () => service.DisplayName,
            serviceName,
            ref isComplete);
        string status = TryRead(
            () => service.Status.ToString(),
            "Unknown",
            ref isComplete);
        bool canStop = TryRead(
            () => service.CanStop,
            false,
            ref isComplete);
        ServiceType controllerServiceType = TryRead(
            () => service.ServiceType,
            (ServiceType)0,
            ref isComplete);
        string[] dependencies = TryRead(
            () => service.ServicesDependedOn
                .Select(dependency => dependency.ServiceName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            [],
            ref isComplete);
        string[] runningDependents = TryRead(
            () => service.DependentServices
                .Where(dependent =>
                    dependent.Status
                        == ServiceControllerStatus.Running)
                .Select(dependent => dependent.ServiceName)
                .OrderBy(
                    name => name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            [],
            ref isComplete);

        ServiceNativeConfiguration? native = null;
        try
        {
            native = ServiceNativeConfigurationReader.Read(serviceName);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            isComplete = false;
        }

        ServiceType serviceType =
            native?.ServiceType ?? controllerServiceType;
        string startType = native?.StartType.ToString()
            ?? TryRead(
                () => service.StartType.ToString(),
                "Unknown",
                ref isComplete);
        bool isDriver =
            (serviceType
                & (ServiceType.KernelDriver
                    | ServiceType.FileSystemDriver
                    | ServiceType.RecognizerDriver)) != 0;
        bool isSharedProcess =
            (serviceType & ServiceType.Win32ShareProcess) != 0;

        return new(
            serviceName,
            displayName,
            status,
            canStop,
            serviceType.ToString(),
            dependencies,
            startType,
            native?.BinaryPath ?? string.Empty,
            native?.ServiceAccount ?? string.Empty,
            isDriver,
            isSharedProcess,
            native?.TriggerCount ?? 0,
            runningDependents,
            isComplete);
    }

    private static T TryRead<T>(
        Func<T> reader,
        T fallback,
        ref bool isComplete)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            isComplete = false;
            return fallback;
        }
    }

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is
            InvalidOperationException
            or Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException;
}

using System.ServiceProcess;

namespace Dismode.Windows.Services;

public sealed class WindowsServiceControlAdapter :
    IWindowsServiceControlAdapter
{
    private static readonly TimeSpan TransitionTimeout =
        TimeSpan.FromSeconds(30);

    public ValueTask<WindowsServiceSnapshot> CaptureAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using ServiceController controller = new(serviceName);
        controller.Refresh();
        ServiceNativeConfiguration native =
            ServiceNativeConfigurationReader.Read(serviceName);
        string[] dependencies = controller.ServicesDependedOn
            .Select(service => service.ServiceName)
            .ToArray();
        string[] runningDependents = controller.DependentServices
            .Where(service =>
                service.Status == ServiceControllerStatus.Running)
            .Select(service => service.ServiceName)
            .ToArray();

        return ValueTask.FromResult(
            new WindowsServiceSnapshot(
                controller.ServiceName,
                controller.DisplayName,
                controller.Status,
                native.StartType,
                native.ServiceType,
                controller.CanStop,
                native.ProcessId,
                native.BinaryPath,
                native.ServiceAccount,
                native.DelayedAutoStart,
                native.TriggerCount,
                dependencies,
                runningDependents));
    }

    public async ValueTask RequestStopAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        using ServiceController controller = new(serviceName);
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        controller.Stop(stopDependentServices: false);
        await WaitForStatusAsync(
                controller,
                ServiceControllerStatus.Stopped,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RequestStartAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        using ServiceController controller = new(serviceName);
        controller.Refresh();
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        controller.Start();
        await WaitForStatusAsync(
                controller,
                ServiceControllerStatus.Running,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask WaitForStatusAsync(
        ServiceController controller,
        ServiceControllerStatus desiredStatus,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransitionTimeout);

        while (true)
        {
            controller.Refresh();
            if (controller.Status == desiredStatus)
            {
                return;
            }

            try
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(250),
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested)
            {
                throw new System.TimeoutException(
                    $"Service '{controller.ServiceName}' did not reach "
                    + $"{desiredStatus} within {TransitionTimeout}.");
            }
        }
    }
}

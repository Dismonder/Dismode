namespace Dismode.Windows.Services;

public interface IWindowsServiceControlAdapter
{
    ValueTask<WindowsServiceSnapshot> CaptureAsync(
        string serviceName,
        CancellationToken cancellationToken);

    ValueTask RequestStopAsync(
        string serviceName,
        CancellationToken cancellationToken);

    ValueTask RequestStartAsync(
        string serviceName,
        CancellationToken cancellationToken);
}

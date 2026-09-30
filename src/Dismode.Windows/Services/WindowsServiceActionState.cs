using System.ServiceProcess;

namespace Dismode.Windows.Services;

public sealed record WindowsServiceActionState(
    ServiceControllerStatus Status,
    string ConfigurationFingerprint);

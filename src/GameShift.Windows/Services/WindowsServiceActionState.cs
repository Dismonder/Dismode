using System.ServiceProcess;

namespace GameShift.Windows.Services;

public sealed record WindowsServiceActionState(
    ServiceControllerStatus Status,
    string ConfigurationFingerprint);

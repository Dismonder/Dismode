using System.ServiceProcess;

namespace GameShift.Windows.Services;

internal sealed record ServiceNativeConfiguration(
    string BinaryPath,
    string ServiceAccount,
    ServiceStartMode StartType,
    ServiceType ServiceType,
    bool DelayedAutoStart,
    int TriggerCount,
    int ProcessId);

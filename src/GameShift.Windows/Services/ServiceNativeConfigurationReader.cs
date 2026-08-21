using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Services;

internal static class ServiceNativeConfigurationReader
{
    private const int ErrorInsufficientBuffer = 122;

    internal static ServiceNativeConfiguration Read(string serviceName)
    {
        using SafeServiceHandle manager = new(
            ServiceNativeMethods.OpenScManager(
                machineName: null,
                databaseName: null,
                ServiceNativeMethods.ScManagerConnect));
        if (manager.IsInvalid)
        {
            throw new Win32Exception();
        }

        using SafeServiceHandle service = new(
            ServiceNativeMethods.OpenService(
                manager,
                serviceName,
                ServiceNativeMethods.ServiceQueryConfig
                    | ServiceNativeMethods.ServiceQueryStatus));
        if (service.IsInvalid)
        {
            throw new Win32Exception();
        }

        QueryServiceConfigNative config = ReadBasicConfiguration(service);
        bool delayedAutoStart = ReadDelayedAutoStart(service);
        int triggerCount = ReadTriggerCount(service);
        int processId = ReadProcessId(service);

        return new(
            Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty,
            Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
            (ServiceStartMode)config.StartType,
            (ServiceType)config.ServiceType,
            delayedAutoStart,
            triggerCount,
            processId);
    }

    private static QueryServiceConfigNative ReadBasicConfiguration(
        SafeServiceHandle service)
    {
        ServiceNativeMethods.QueryServiceConfig(
            service,
            serviceConfig: 0,
            bufferSize: 0,
            out uint bytesNeeded);
        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorInsufficientBuffer || bytesNeeded == 0)
        {
            throw new Win32Exception(error);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        try
        {
            if (ServiceNativeMethods.QueryServiceConfig(
                    service,
                    buffer,
                    bytesNeeded,
                    out _) == 0)
            {
                throw new Win32Exception();
            }

            return Marshal.PtrToStructure<QueryServiceConfigNative>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadDelayedAutoStart(SafeServiceHandle service)
    {
        nint buffer = QueryConfiguration2(
            service,
            ServiceNativeMethods.ServiceConfigDelayedAutoStartInfo);
        if (buffer == 0)
        {
            return false;
        }

        try
        {
            ServiceDelayedAutoStartInfo info =
                Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(buffer);
            return info.IsDelayedAutoStart != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ReadTriggerCount(SafeServiceHandle service)
    {
        nint buffer = QueryConfiguration2(
            service,
            ServiceNativeMethods.ServiceConfigTriggerInfo);
        if (buffer == 0)
        {
            return 0;
        }

        try
        {
            ServiceTriggerInfo info =
                Marshal.PtrToStructure<ServiceTriggerInfo>(buffer);
            return checked((int)info.TriggerCount);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static nint QueryConfiguration2(
        SafeServiceHandle service,
        uint infoLevel)
    {
        int initialResult = ServiceNativeMethods.QueryServiceConfig2(
            service,
            infoLevel,
            buffer: 0,
            bufferSize: 0,
            out uint bytesNeeded);
        if (initialResult != 0 || bytesNeeded == 0)
        {
            return 0;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        nint buffer = Marshal.AllocHGlobal(checked((int)bytesNeeded));
        if (ServiceNativeMethods.QueryServiceConfig2(
                service,
                infoLevel,
                buffer,
                bytesNeeded,
                out _) != 0)
        {
            return buffer;
        }

        int finalError = Marshal.GetLastPInvokeError();
        Marshal.FreeHGlobal(buffer);
        throw new Win32Exception(finalError);
    }

    private static int ReadProcessId(SafeServiceHandle service)
    {
        if (ServiceNativeMethods.QueryServiceStatusEx(
                service,
                ServiceNativeMethods.ScStatusProcessInfo,
                out ServiceStatusProcess status,
                checked((uint)Marshal.SizeOf<ServiceStatusProcess>()),
                out _) == 0)
        {
            throw new Win32Exception();
        }

        return checked((int)status.ProcessId);
    }
}

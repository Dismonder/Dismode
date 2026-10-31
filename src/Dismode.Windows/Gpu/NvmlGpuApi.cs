using System.Buffers.Text;
using System.Runtime.InteropServices;

namespace Dismode.Windows.Gpu;

internal interface INvmlSensorApi
{
    uint Temperature(nint device, out uint value);
    uint Clock(nint device, out uint value);
    uint Power(nint device, out uint value);
    uint Fan(nint device, out uint value);
}

internal sealed unsafe class NvmlGpuApi(nint module) : IVendorGpuApi, INvmlSensorApi
{
    private readonly StatusCall _init = Export<StatusCall>(module, "nvmlInit_v2");
    private readonly StatusCall _shutdown = Export<StatusCall>(module, "nvmlShutdown");
    private readonly CountCall _count = Export<CountCall>(module, "nvmlDeviceGetCount_v2");
    private readonly DeviceCall _device = Export<DeviceCall>(module, "nvmlDeviceGetHandleByIndex_v2");
    private readonly PciCall _pci = Export<PciCall>(module, "nvmlDeviceGetPciInfo_v3");
    private readonly TemperatureCall? _temperature = Optional<TemperatureCall>(module, "nvmlDeviceGetTemperature");
    private readonly ClockCall? _clock = Optional<ClockCall>(module, "nvmlDeviceGetClockInfo");
    private readonly SensorCall? _power = Optional<SensorCall>(module, "nvmlDeviceGetPowerUsage");
    private readonly SensorCall? _fan = Optional<SensorCall>(module, "nvmlDeviceGetFanSpeed");
    private VendorAdapter[] _adapters = [];
    private nint[] _devices = [];
    private bool _initialized;

    public bool Initialize()
    {
        _initialized = _init() == 0;
        if (!_initialized || _count(out uint count) != 0 || count is 0 or > 64) { return false; }
        _adapters = new VendorAdapter[count];
        _devices = new nint[count];
        return true;
    }

    public bool TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters)
    {
        adapters = default;
        if (_count(out uint count) != 0 || count != _devices.Length) { return false; }
        int written = 0;
        for (uint index = 0; index < count; index++)
        {
            PciInfo pci = default;
            if (_device(index, out _devices[index]) != 0 || _pci(_devices[index], &pci) != 0) { return false; }
            ReadOnlySpan<byte> busId = new(pci.BusId, 32);
            int end = busId.IndexOf((byte)0);
            if (end < 0) { continue; }
            busId = busId[..end];
            int dot = busId.LastIndexOf((byte)'.');
            if (dot < 0 || !Utf8Parser.TryParse(busId[(dot + 1)..], out uint function, out int consumed, 'x')
                || consumed != busId.Length - dot - 1 || function > 7) { continue; }
            _adapters[written++] = new((int)index, new(pci.Domain, pci.Bus, pci.Device, function));
        }

        adapters = _adapters.AsSpan(0, written);
        return true;
    }

    public VendorReadStatus Read(int device, out GpuVendorTelemetrySnapshot snapshot)
    {
        return ReadSensors(this, _devices[device], out snapshot);
    }

    internal static VendorReadStatus ReadSensors(INvmlSensorApi api, nint handle, out GpuVendorTelemetrySnapshot snapshot)
    {
        uint ts = api.Temperature(handle, out uint temperature);
        uint cs = api.Clock(handle, out uint clock);
        uint ps = api.Power(handle, out uint power);
        uint fs = api.Fan(handle, out uint fan);
        snapshot = new(ts == 0 ? temperature : null, cs == 0 ? clock : null,
            ps == 0 ? power / 1000d : null, fs == 0 ? fan : null);
        // NVML_ERROR_GPU_IS_LOST = 15; pozostale bledy czujnika nie tworza wartosci.
        return ts == 15 || cs == 15 || ps == 15 || fs == 15 ? VendorReadStatus.Removed : VendorReadStatus.Success;
    }

    public uint Temperature(nint device, out uint value) { value = 0; return _temperature?.Invoke(device, 0, out value) ?? 3; }
    public uint Clock(nint device, out uint value) { value = 0; return _clock?.Invoke(device, 0, out value) ?? 3; }
    public uint Power(nint device, out uint value) { value = 0; return _power?.Invoke(device, out value) ?? 3; }
    public uint Fan(nint device, out uint value) { value = 0; return _fan?.Invoke(device, out value) ?? 3; }

    public void Dispose()
    {
        if (_initialized) { _ = _shutdown(); _initialized = false; }
    }

    private static T Export<T>(nint module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    private static T? Optional<T>(nint module, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(module, name, out nint address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        internal fixed byte LegacyBusId[16];
        internal uint Domain;
        internal uint Bus;
        internal uint Device;
        internal uint DeviceId;
        internal uint SubSystemId;
        internal fixed byte BusId[32];
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint StatusCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CountCall(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint DeviceCall(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint PciCall(nint device, PciInfo* pci);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SensorCall(nint device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint TemperatureCall(nint device, uint sensor, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint ClockCall(nint device, uint clock, out uint value);
}

using System.Runtime.InteropServices;

namespace Dismode.Windows.Gpu;

internal sealed unsafe class AdlGpuApi(nint module) : IVendorGpuApi
{
    private readonly CreateCall _create = Export<CreateCall>(module, "ADL2_Main_Control_Create");
    private readonly DestroyCall _destroy = Export<DestroyCall>(module, "ADL2_Main_Control_Destroy");
    private readonly CountCall _count = Export<CountCall>(module, "ADL2_Adapter_NumberOfAdapters_Get");
    private readonly InfoCall _info = Export<InfoCall>(module, "ADL2_Adapter_AdapterInfo_Get");
    private readonly LogCall _log = Export<LogCall>(module, "ADL2_New_QueryPMLogData_Get");
    private readonly AllocationPool _allocator = new();
    private AdapterInfo[] _infoBuffer = [];
    private VendorAdapter[] _adapters = [];
    private nint _context;

    public bool Initialize()
    {
        if (_create(_allocator.Callback, 1, out _context) != 0 || _context == 0
            || _count(_context, out int count) != 0 || count is <= 0 or > 256) { return false; }
        _infoBuffer = new AdapterInfo[count];
        _adapters = new VendorAdapter[count];
        return true;
    }

    public bool TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters)
    {
        adapters = default;
        if (_count(_context, out int count) != 0 || count != _infoBuffer.Length) { return false; }
        int written = 0;
        fixed (AdapterInfo* buffer = _infoBuffer)
        {
            new Span<AdapterInfo>(buffer, count).Clear();
            for (int index = 0; index < count; index++) { buffer[index].Size = sizeof(AdapterInfo); }
            if (_info(_context, buffer, count * sizeof(AdapterInfo)) != 0) { return false; }
            for (int index = 0; index < count; index++)
            {
                AdapterInfo info = buffer[index];
                if (info.Present != 1 || info.Exists != 1 || info.VendorId != 1002
                    || info.Bus is < 0 or > 255 || info.Device is < 0 or > 31 || info.Function is < 0 or > 7) { continue; }
                GpuPciAddress address = new(0, (uint)info.Bus, (uint)info.Device, (uint)info.Function);
                // ADL moze zwrocic kilka uchwytow wyswietlaczy tego samego fizycznego GPU.
                bool duplicate = false;
                for (int prior = 0; prior < written; prior++) { duplicate |= _adapters[prior].Address == address; }
                if (!duplicate) { _adapters[written++] = new(info.Index, address); }
            }
        }

        adapters = _adapters.AsSpan(0, written);
        return true;
    }

    public VendorReadStatus Read(int device, out GpuVendorTelemetrySnapshot snapshot)
    {
        PmLog output = default;
        output.Size = sizeof(PmLog);
        int status = _log(_context, device, &output);
        return Decode(status, new ReadOnlySpan<int>(output.Sensors, 512), out snapshot);
    }

    internal static VendorReadStatus Decode(int status, ReadOnlySpan<int> sensors, out GpuVendorTelemetrySnapshot snapshot)
    {
        snapshot = default;
        if (status != 0)
        {
            return status switch
            {
                -8 => VendorReadStatus.Unsupported,
                -5 or -10 => VendorReadStatus.Removed,
                _ => VendorReadStatus.Error,
            };
        }
        // PMLog: stopnie C, MHz, W i procenty. Tylko BOARD_POWER (73), nigdy ASIC_POWER (23).
        snapshot = Decode(sensors);
        return VendorReadStatus.Success;
    }

    internal static GpuVendorTelemetrySnapshot Decode(ReadOnlySpan<int> sensors) =>
        new(Sensor(sensors, 8), Sensor(sensors, 1), Sensor(sensors, 73), Sensor(sensors, 15));

    private static double? Sensor(ReadOnlySpan<int> sensors, int index) =>
        sensors[index * 2] != 0 ? sensors[index * 2 + 1] : null;

    public void Dispose()
    {
        if (_context != 0) { _ = _destroy(_context); _context = 0; }
        _allocator.Dispose();
    }

    private sealed class AllocationPool : IDisposable
    {
        private readonly List<nint> _buffers = [];
        internal AllocateCall Callback { get; }
        internal AllocationPool() => Callback = Allocate;
        private nint Allocate(int size)
        {
            if (size <= 0) { return 0; }
            nint buffer = Marshal.AllocHGlobal(size);
            _buffers.Add(buffer);
            return buffer;
        }

        public void Dispose()
        {
            // Bufory zwrocone przez callback naleza do wywolujacego ADL.
            foreach (nint buffer in _buffers) { Marshal.FreeHGlobal(buffer); }
            _buffers.Clear();
            GC.KeepAlive(Callback);
        }
    }

    private static T Export<T>(nint module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        internal int Size;
        internal int Index;
        internal fixed byte Udid[256];
        internal int Bus;
        internal int Device;
        internal int Function;
        internal int VendorId;
        internal fixed byte Name[256];
        internal fixed byte DisplayName[256];
        internal int Present;
        internal int Exists;
        internal fixed byte DriverPath[256];
        internal fixed byte DriverPathExt[256];
        internal fixed byte PnpString[256];
        internal int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PmLog { internal int Size; internal fixed int Sensors[512]; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint AllocateCall(int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateCall(AllocateCall allocate, int connectedOnly, out nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyCall(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountCall(nint context, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InfoCall(nint context, AdapterInfo* info, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LogCall(nint context, int adapter, PmLog* log);
}

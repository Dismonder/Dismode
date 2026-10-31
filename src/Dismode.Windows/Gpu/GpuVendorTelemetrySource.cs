namespace Dismode.Windows.Gpu;

public readonly record struct GpuVendorTelemetrySnapshot(
    double? TemperatureCelsius, double? CoreClockMHz, double? BoardPowerWatts, double? FanPercent);

internal enum GpuVendor { Nvidia, Amd }
internal enum VendorReadStatus { Success, Unsupported, Removed, Error }
internal readonly record struct GpuPciAddress(uint Domain, uint Bus, uint Device, uint Function);
internal readonly record struct VendorAdapter(int Index, GpuPciAddress Address);

internal interface IGpuAdapterAddressResolver
{
    bool TryResolve(GpuAdapterId adapter, out GpuPciAddress address);
}

internal interface ITrustedVendorLibrary
{
    bool TryLoad(GpuVendor vendor, out nint module);
    void Unload(nint module);
}

internal interface IVendorGpuApi : IDisposable
{
    bool Initialize();
    bool TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters);
    VendorReadStatus Read(int device, out GpuVendorTelemetrySnapshot snapshot);
}

internal interface IGpuVendorTelemetrySource : IDisposable
{
    GpuVendorTelemetrySnapshot? Sample(GpuAdapterId adapter);
    void Stop();
}

internal sealed class GpuVendorTelemetrySource : IGpuVendorTelemetrySource
{
    private readonly VendorGpuDeviceSource _nvidia = new(GpuVendor.Nvidia, new SystemVendorLibrary(),
        module => new NvmlGpuApi(module), new SystemGpuAdapterAddressResolver());
    private readonly VendorGpuDeviceSource _amd = new(GpuVendor.Amd, new SystemVendorLibrary(),
        module => new AdlGpuApi(module), new SystemGpuAdapterAddressResolver());

    public GpuVendorTelemetrySnapshot? Sample(GpuAdapterId adapter)
    {
        GpuVendorTelemetrySnapshot? nvidia = _nvidia.Sample(adapter);
        GpuVendorTelemetrySnapshot? amd = _amd.Sample(adapter);
        // Rozne biblioteki nie powinny wskazywac tego samego adaptera.
        return nvidia is not null && amd is not null ? null : nvidia ?? amd;
    }

    public void Stop() { _nvidia.Stop(); _amd.Stop(); }
    public void Dispose() { _nvidia.Dispose(); _amd.Dispose(); }
}

internal sealed class VendorGpuDeviceSource(
    GpuVendor vendor, ITrustedVendorLibrary library, Func<nint, IVendorGpuApi> createApi,
    IGpuAdapterAddressResolver resolver) : IGpuVendorTelemetrySource
{
    private nint _module;
    private IVendorGpuApi? _api;
    private bool _failed;
    private bool _disposed;

    public GpuVendorTelemetrySnapshot? Sample(GpuAdapterId adapter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed || adapter.PhysicalIndex != 0)
        {
            return null;
        }

        try
        {
            if (!resolver.TryResolve(adapter, out GpuPciAddress address)) { return null; }
            if (_api is null)
            {
                if (!library.TryLoad(vendor, out _module)) { return Fail(); }
                _api = createApi(_module);
                if (!_api.Initialize()) { return Fail(); }
            }

            if (!_api.TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters)) { return Fail(); }
            int? selected = null;
            foreach (VendorAdapter candidate in adapters)
            {
                if (candidate.Address == address)
                {
                    if (selected is not null) { return null; }
                    selected = candidate.Index;
                }
            }

            if (selected is not int index) { return null; }
            VendorReadStatus status = _api.Read(index, out GpuVendorTelemetrySnapshot result);
            if (status is VendorReadStatus.Removed or VendorReadStatus.Error) { return Fail(); }
            if (status != VendorReadStatus.Success) { return null; }
            result = new(Valid(result.TemperatureCelsius, -50, 150), Valid(result.CoreClockMHz, 0, 20000),
                Valid(result.BoardPowerWatts, 0, 3000), Valid(result.FanPercent, 0, 100));
            return result == default ? null : result;
        }
        catch (Exception)
        {
            // Awaria opcjonalnego zrodla nie moze wylaczyc PDH ani uruchamiac petli ponowien.
            return Fail();
        }
    }

    private static double? Valid(double? value, double minimum, double maximum) =>
        value is double number && double.IsFinite(number) && number >= minimum && number <= maximum ? number : null;

    private GpuVendorTelemetrySnapshot? Fail()
    {
        _failed = true;
        Stop();
        return null;
    }

    public void Stop()
    {
        try { _api?.Dispose(); }
        finally
        {
            _api = null;
            if (_module != 0) { library.Unload(_module); _module = 0; }
        }
    }

    public void Dispose()
    {
        if (!_disposed) { Stop(); _disposed = true; }
    }
}

using System.Runtime.InteropServices;

namespace Dismode.Windows.Gpu;

internal sealed partial class SystemGpuAdapterAddressResolver : IGpuAdapterAddressResolver
{
    public unsafe bool TryResolve(GpuAdapterId adapter, out GpuPciAddress address)
    {
        address = default;
        if (adapter.PhysicalIndex != 0) { return false; }
        OpenAdapter open = new() { Luid = adapter.Luid };
        if (D3DKMTOpenAdapterFromLuid(ref open) != 0) { return false; }
        try
        {
            AdapterAddress pci = default;
            QueryAdapter query = new()
            {
                Handle = open.Handle,
                Type = 6, // KMTQAITYPE_ADAPTERADDRESS
                Data = (nint)(&pci),
                Size = (uint)sizeof(AdapterAddress),
            };
            if (D3DKMTQueryAdapterInfo(ref query) != 0 || pci.Bus > 255 || pci.Device > 31 || pci.Function > 7)
            {
                return false;
            }

            address = new(0, pci.Bus, pci.Device, pci.Function);
            return true;
        }
        finally
        {
            uint handle = open.Handle;
            _ = D3DKMTCloseAdapter(ref handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { internal ulong Luid; internal uint Handle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterAddress { internal uint Bus; internal uint Device; internal uint Function; }
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapter { internal uint Handle; internal int Type; internal nint Data; internal uint Size; }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(ref OpenAdapter open);
    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref QueryAdapter query);
    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref uint handle);
}

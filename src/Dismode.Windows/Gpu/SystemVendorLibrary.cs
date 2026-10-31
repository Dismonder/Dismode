using System.Reflection;
using System.Runtime.InteropServices;
using Dismode.Windows.Security;

namespace Dismode.Windows.Gpu;

internal interface ISystemVendorLibraryApi
{
    IDisposable OpenReadOnly(string path);
    AuthenticodeVerificationResult Verify(string path);
    nint Load(string path, uint flags);
    void Free(nint module);
}

internal sealed class SystemVendorLibrary(bool allowTestProcess = false, ISystemVendorLibraryApi? api = null)
    : ITrustedVendorLibrary, IDisposable
{
    private readonly ISystemVendorLibraryApi _api = api ?? new NativeSystemVendorLibraryApi();
    private IDisposable? _file;

    public bool TryLoad(GpuVendor vendor, out nint module)
    {
        module = 0;
        if (!allowTestProcess && Assembly.GetEntryAssembly()?.GetName().Name != "Dismode.UI")
        {
            return false;
        }

        string path = Path.Combine(Environment.SystemDirectory, vendor == GpuVendor.Nvidia ? "nvml.dll" : "atiadlxx.dll");
        try
        {
            // Blokada zapisu i podmiany obejmuje weryfikacje oraz caly czas zycia modulu.
            _file = _api.OpenReadOnly(path);
            AuthenticodeVerificationResult signature = _api.Verify(path);
            if (!IsTrustedSigner(vendor, signature.HasValidAuthenticodeSignature, signature.SignerSubject))
            {
                _file.Dispose();
                _file = null;
                return false;
            }

            module = _api.Load(path, 0x00000800); // LOAD_LIBRARY_SEARCH_SYSTEM32
            if (module != 0) { return true; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            // Brak pliku lub zaufania oznacza brak zrodla do konca zycia samplera.
        }

        _file?.Dispose();
        _file = null;
        return false;
    }

    internal static bool IsTrustedSigner(GpuVendor vendor, bool valid, string? signer) => valid && (vendor switch
    {
        GpuVendor.Nvidia => string.Equals(signer, "NVIDIA Corporation", StringComparison.OrdinalIgnoreCase),
        GpuVendor.Amd => string.Equals(signer, "Advanced Micro Devices, Inc.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signer, "Advanced Micro Devices Inc.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(signer, "Advanced Micro Devices", StringComparison.OrdinalIgnoreCase),
        _ => false,
    });

    public void Unload(nint module)
    {
        _api.Free(module);
        _file?.Dispose();
        _file = null;
    }

    public void Dispose() { _file?.Dispose(); _file = null; }
}

internal sealed partial class NativeSystemVendorLibraryApi : ISystemVendorLibraryApi
{
    public IDisposable OpenReadOnly(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public AuthenticodeVerificationResult Verify(string path) => new AuthenticodeSignatureVerifier().Verify(path);
    public nint Load(string path, uint flags) => LoadLibraryExW(path, 0, flags);
    public void Free(nint module) => _ = FreeLibrary(module);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}

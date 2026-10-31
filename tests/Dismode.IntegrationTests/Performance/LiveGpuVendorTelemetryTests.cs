using Dismode.Windows.Gpu;
using Dismode.Windows.Security;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class LiveGpuVendorTelemetryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Live")]
    public void DriverInstalledVendorLibrariesReadOrReportUnavailableCleanly()
    {
        foreach (GpuVendor vendor in new[] { GpuVendor.Nvidia, GpuVendor.Amd })
        {
            string path = Path.Combine(Environment.SystemDirectory, vendor == GpuVendor.Nvidia ? "nvml.dll" : "atiadlxx.dll");
            AuthenticodeVerificationResult signature = new AuthenticodeSignatureVerifier().Verify(path);
            using SystemVendorLibrary library = new(allowTestProcess: true);
            bool loaded = library.TryLoad(vendor, out nint module);
            TestContext.WriteLine($"{vendor}: exists={File.Exists(path)}, signature={signature.HasValidAuthenticodeSignature}, signer={signature.SignerSubject}, loaded={loaded}");
            if (!loaded)
            {
                Assert.AreEqual(0, module);
                Assert.IsFalse(File.Exists(path) && SystemVendorLibrary.IsTrustedSigner(vendor,
                    signature.HasValidAuthenticodeSignature, signature.SignerSubject), "A trusted installed library should load.");
                continue;
            }

            try
            {
                using IVendorGpuApi api = vendor == GpuVendor.Nvidia ? new NvmlGpuApi(module) : new AdlGpuApi(module);
                Assert.IsTrue(api.Initialize());
                Assert.IsTrue(api.TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters));
                Assert.IsFalse(adapters.IsEmpty);
                foreach (VendorAdapter adapter in adapters)
                {
                    VendorReadStatus status = api.Read(adapter.Index, out GpuVendorTelemetrySnapshot sample);
                    TestContext.WriteLine($"{vendor}: PCI={adapter.Address}, status={status}, sensors={sample}");
                    Assert.AreEqual(VendorReadStatus.Success, status);
                    Assert.AreNotEqual(default, sample, "At least one vendor sensor should be readable.");
                }
            }
            finally { library.Unload(module); }
        }
    }
}

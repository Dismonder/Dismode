using System.Globalization;
using Dismode.Windows.Gpu;
using Dismode.Windows.Security;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class GpuVendorTelemetryTests
{
    [TestMethod]
    public void OverlayTextShowsOnlyValuesReadSuccessfully()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.IsNull(GpuVendorTelemetryFormatter.Format(null));
            Assert.IsNull(GpuVendorTelemetryFormatter.Format(new(null, null, null, null)));
            Assert.IsNull(GpuVendorTelemetryFormatter.Format(new(double.NaN, null, null, null)));
            Assert.AreEqual("GPU 65 °C | 2400 MHz | 180 W | 40% went.",
                GpuVendorTelemetryFormatter.Format(new(65.2, 2400.4, 180.1, 40)));
            Assert.AreEqual("GPU 2400 MHz | 180 W", GpuVendorTelemetryFormatter.Format(new(null, 2400, 180, null)));
            Assert.AreEqual("GPU 40% went.", GpuVendorTelemetryFormatter.Format(new(null, null, null, 40)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void NativeLoaderChecksLockedSystemFileAndVendorBeforeLoading()
    {
        FakeSystemLibraryApi api = new();
        using SystemVendorLibrary library = new(allowTestProcess: true, api);
        Assert.IsTrue(library.TryLoad(GpuVendor.Amd, out nint module));
        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"), api.Path);
        Assert.AreEqual(0x800u, api.Flags);
        Assert.IsTrue(api.LockedDuringVerify);
        Assert.IsTrue(api.VerifiedBeforeLoad);
        library.Unload(module);
        Assert.IsFalse(api.Locked);

        api.Valid = false;
        Assert.IsFalse(library.TryLoad(GpuVendor.Amd, out _));
        Assert.AreEqual(1, api.Loads);
        Assert.IsFalse(api.Locked);
        api.Missing = true;
        Assert.IsFalse(library.TryLoad(GpuVendor.Amd, out _));
        Assert.AreEqual(1, api.Loads);
        Assert.IsFalse(api.Locked);
    }

    [TestMethod]
    public void NativeLoaderRefusesNonUiProcessesBeforeOpeningDriverFile()
    {
        FakeSystemLibraryApi api = new();
        using SystemVendorLibrary library = new(api: api);
        Assert.IsFalse(library.TryLoad(GpuVendor.Amd, out _));
        Assert.IsNull(api.Path);
    }

    private sealed class FakeSystemLibraryApi : ISystemVendorLibraryApi, IDisposable
    {
        internal string? Path { get; private set; }
        internal uint Flags { get; private set; }
        internal bool Missing { get; set; }
        internal bool Valid { get; set; } = true;
        internal bool Locked { get; private set; }
        internal bool LockedDuringVerify { get; private set; }
        internal bool VerifiedBeforeLoad { get; private set; }
        internal int Loads { get; private set; }
        public IDisposable OpenReadOnly(string path)
        {
            Path = path;
            if (Missing) { throw new FileNotFoundException(); }
            Locked = true;
            return this;
        }

        public AuthenticodeVerificationResult Verify(string path)
        {
            LockedDuringVerify = Locked;
            return new(Valid, false, null, "fake", "Advanced Micro Devices, Inc.");
        }

        public nint Load(string path, uint flags)
        {
            Flags = flags;
            Loads++;
            VerifiedBeforeLoad = LockedDuringVerify;
            return 123;
        }

        public void Free(nint module) => Assert.IsTrue(Locked);
        public void Dispose() => Locked = false;
    }

    [TestMethod]
    public void NvmlNotSupportedHidesOnlyFailedSensorsAndGpuLostDiscardsSample()
    {
        FakeNvml api = new();
        Assert.AreEqual(VendorReadStatus.Success, NvmlGpuApi.ReadSensors(api, 1, out GpuVendorTelemetrySnapshot sample));
        Assert.AreEqual(new GpuVendorTelemetrySnapshot(60, null, 300, null), sample);
        api.TemperatureStatus = 15;
        Assert.AreEqual(VendorReadStatus.Removed, NvmlGpuApi.ReadSensors(api, 1, out _));
    }

    private sealed class FakeNvml : INvmlSensorApi
    {
        internal uint TemperatureStatus { get; set; }
        public uint Temperature(nint device, out uint value) { value = 60; return TemperatureStatus; }
        public uint Clock(nint device, out uint value) { value = 9999; return 3; }
        public uint Power(nint device, out uint value) { value = 300000; return 0; }
        public uint Fan(nint device, out uint value) { value = 99; return 3; }
    }

    [TestMethod]
    public void OnlyValidExactVendorSignersAreAllowed()
    {
        Assert.IsTrue(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Nvidia, true, "NVIDIA Corporation"));
        Assert.IsTrue(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Amd, true, "Advanced Micro Devices, Inc."));
        Assert.IsTrue(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Amd, true, "Advanced Micro Devices"));
        Assert.IsFalse(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Nvidia, false, "NVIDIA Corporation"));
        Assert.IsFalse(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Amd, true, "Microsoft Windows Hardware Compatibility Publisher"));
        Assert.IsFalse(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Amd, true, "Advanced Micro Devices Inc. attacker"));
        Assert.IsFalse(SystemVendorLibrary.IsTrustedSigner(GpuVendor.Nvidia, true, null));
    }

    [TestMethod]
    public void AdlLogUsesSupportedFlagsAndBoardPowerRatherThanAsicPower()
    {
        int[] log = new int[512];
        log[8 * 2] = 1;
        log[8 * 2 + 1] = 60;
        log[1 * 2 + 1] = 9999;
        log[23 * 2] = 1;
        log[23 * 2 + 1] = 200;
        Assert.AreEqual(new GpuVendorTelemetrySnapshot(60, null, null, null), AdlGpuApi.Decode(log));
        log[73 * 2] = 1;
        log[73 * 2 + 1] = 300;
        Assert.AreEqual(300d, AdlGpuApi.Decode(log).BoardPowerWatts);
        Assert.AreEqual(VendorReadStatus.Unsupported, AdlGpuApi.Decode(-8, log, out GpuVendorTelemetrySnapshot unsupported));
        Assert.AreEqual(default, unsupported);
        Assert.AreEqual(VendorReadStatus.Removed, AdlGpuApi.Decode(-5, log, out _));
        Assert.AreEqual(VendorReadStatus.Error, AdlGpuApi.Decode(-1, log, out _));
    }

    [TestMethod]
    public void MissingLibraryAndBadSignatureNeverCallVendorApiOrRetry()
    {
        foreach (bool missing in new[] { true, false })
        {
            FakeLibrary library = new() { Exists = !missing, Trusted = missing };
            FakeApi api = new();
            using VendorGpuDeviceSource source = Create(library, api);
            for (int i = 0; i < 10; i++)
            {
                Assert.IsNull(source.Sample(new(1, 0)));
                source.Stop();
            }

            Assert.AreEqual(0, library.Loads);
            Assert.AreEqual(0, api.Initializations);
            Assert.AreEqual(1, library.Attempts);
        }
    }

    [TestMethod]
    public void InitializationFailureUnloadsAndDoesNotRetry()
    {
        FakeLibrary library = new();
        FakeApi api = new() { InitializeResult = false };
        using VendorGpuDeviceSource source = Create(library, api);
        Assert.IsNull(source.Sample(new(1, 0)));
        Assert.IsNull(source.Sample(new(1, 0)));
        Assert.AreEqual(1, api.Initializations);
        Assert.AreEqual(1, api.Disposals);
        Assert.AreEqual(1, library.Unloads);
    }

    [TestMethod]
    public void UnsupportedSensorsHideIndividuallyAndRemovedAdapterDisablesSource()
    {
        FakeLibrary library = new();
        FakeApi api = new() { Reading = new(60, null, null, 0) };
        using VendorGpuDeviceSource source = Create(library, api);
        Assert.AreEqual(api.Reading, source.Sample(new(1, 0)));
        api.ReadStatus = VendorReadStatus.Unsupported;
        Assert.IsNull(source.Sample(new(1, 0)));
        api.ReadStatus = VendorReadStatus.Removed;
        Assert.IsNull(source.Sample(new(1, 0)));
        api.ReadStatus = VendorReadStatus.Success;
        Assert.IsNull(source.Sample(new(1, 0)));
        Assert.AreEqual(1, library.Unloads);
    }

    [TestMethod]
    public void MapsExactPciAddressAndRefusesUnknownLinkedAndAmbiguousAdapters()
    {
        FakeApi api = new()
        {
            Devices = [new(10, new(0, 2, 0, 0)), new(20, new(0, 1, 0, 0))],
        };
        using VendorGpuDeviceSource source = Create(new(), api);
        Assert.IsNotNull(source.Sample(new(1, 0)));
        Assert.AreEqual(20, api.LastDevice);
        Assert.IsNull(source.Sample(new(99, 0)));
        Assert.IsNull(source.Sample(new(1, 1)));
        api.Devices = [new(10, new(0, 1, 0, 0)), new(20, new(0, 1, 0, 0))];
        Assert.IsNull(source.Sample(new(1, 0)));
        api.Devices = [];
        Assert.IsNull(source.Sample(new(1, 0)));
    }

    [TestMethod]
    public void StopShutsDownBeforeUnloadAndCanReopenSuccessfulLibrary()
    {
        FakeLibrary library = new();
        FakeApi api = new();
        using VendorGpuDeviceSource source = Create(library, api);
        Assert.AreEqual(0, library.Attempts);
        Assert.IsNotNull(source.Sample(new(1, 0)));
        source.Stop();
        Assert.AreEqual(1, api.Disposals);
        Assert.AreEqual(1, library.Unloads);
        Assert.IsNotNull(source.Sample(new(1, 0)));
        Assert.AreEqual(2, library.Loads);
    }

    internal static VendorGpuDeviceSource Create(FakeLibrary library, FakeApi api) =>
        new(GpuVendor.Amd, library, _ => api, new FakeAddressResolver());

    internal sealed class FakeLibrary : ITrustedVendorLibrary
    {
        internal bool Exists { get; init; } = true;
        internal bool Trusted { get; init; } = true;
        internal int Attempts { get; private set; }
        internal int Loads { get; private set; }
        internal int Unloads { get; private set; }
        public bool TryLoad(GpuVendor vendor, out nint module)
        {
            Attempts++;
            module = 0;
            if (!Exists || !Trusted) { return false; }
            Loads++;
            module = 123;
            return true;
        }

        public void Unload(nint module) => Unloads++;
    }

    internal sealed class FakeAddressResolver : IGpuAdapterAddressResolver
    {
        public bool TryResolve(GpuAdapterId adapter, out GpuPciAddress address)
        {
            address = new(0, 1, 0, 0);
            return adapter == new GpuAdapterId(1, 0);
        }
    }

    internal sealed class FakeApi : IVendorGpuApi
    {
        internal bool InitializeResult { get; init; } = true;
        internal int Initializations { get; private set; }
        internal int Disposals { get; private set; }
        internal int LastDevice { get; private set; }
        internal VendorAdapter[] Devices { get; set; } = [new(0, new(0, 1, 0, 0))];
        internal GpuVendorTelemetrySnapshot Reading { get; set; } = new(60, 2500, 300, 50);
        internal VendorReadStatus ReadStatus { get; set; }
        public bool Initialize() { Initializations++; return InitializeResult; }
        public bool TryGetAdapters(out ReadOnlySpan<VendorAdapter> adapters) { adapters = Devices; return true; }
        public VendorReadStatus Read(int device, out GpuVendorTelemetrySnapshot snapshot)
        {
            LastDevice = device;
            snapshot = Reading;
            return ReadStatus;
        }

        public void Dispose() => Disposals++;
    }
}

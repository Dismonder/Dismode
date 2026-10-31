using Dismode.Windows.Gpu;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
public sealed class PdhGpuCounterSourceTests
{
    [TestMethod]
    public void RawArraysReuseBuffersHonorStatusAndDropRemovedInstances()
    {
        ReplayPdhGpuApi api = new();
        using PdhGpuCounterSource source = new(api);
        using GpuTelemetrySampler sampler = new(source);
        _ = sampler.Sample(42);
        int requests = api.ResizeRequests;
        GpuTelemetrySnapshot load = sampler.Sample(42)!.Value;
        Assert.AreEqual(new GpuAdapterId(1, 0), load.Adapter);
        Assert.AreEqual(32d, load.ThreeDPercent);
        Assert.AreEqual(512UL * 1024 * 1024, load.DedicatedBytes);
        Assert.AreEqual(requests, api.ResizeRequests);
        Assert.AreEqual(1, api.Opens);
        api.ItemStatus = 0xC0000BC6;
        Assert.IsNull(sampler.Sample(42));
        api.ItemStatus = 0;
        Assert.IsNull(sampler.Sample(42)!.Value.ThreeDPercent);
        Assert.IsNotNull(sampler.Sample(42)!.Value.ThreeDPercent);
        api.Removed = true;
        Assert.IsNull(sampler.Sample(42));
        api.Removed = false;
        Assert.IsNull(sampler.Sample(42)!.Value.ThreeDPercent);
    }

    [TestMethod]
    public void QueryIsReusedClosedOnStopAndReopenedOnNextConsumer()
    {
        FakePdh api = new();
        using (PdhGpuCounterSource source = new(api))
        {
            for (int index = 0; index < 100; index++)
            {
                Assert.IsTrue(source.TryRead(out _));
            }

            Assert.AreEqual(1, api.Opens);
            Assert.AreEqual(3, api.Adds);
            Assert.AreEqual(0, api.Closes);
            source.Stop();
            Assert.AreEqual(1, api.Closes);
            Assert.IsTrue(source.TryRead(out _));
            Assert.AreEqual(2, api.Opens);
        }

        Assert.AreEqual(2, api.Closes);
    }

    [TestMethod]
    public void FailedSetupAndCollectCloseHandlesAndBackOff()
    {
        FakePdh api = new() { AddStatus = 1 };
        using PdhGpuCounterSource source = new(api);
        Assert.IsFalse(source.TryRead(out _));
        Assert.AreEqual(1, api.Closes);
        Assert.IsFalse(source.TryRead(out _));
        Assert.AreEqual(1, api.Opens);
        api.AddStatus = 0;
        source.Stop();
        api.CollectStatus = 1;
        Assert.IsFalse(source.TryRead(out _));
        Assert.AreEqual(2, api.Closes);
        source.Stop();
        api.CollectStatus = 0;
        Assert.IsTrue(source.TryRead(out _));
    }

    [TestMethod]
    [DataRow("pid_42_luid_0x00000001_0x00000002_phys_0_eng_3_engtype_3D", 0, true)]
    [DataRow("luid_0x00000001_0x00000002_phys_0", 1, true)]
    [DataRow("pid_42_luid_0x00000001_0x00000002_phys_0", 2, true)]
    [DataRow("_Total", 1, false)]
    [DataRow("pid_bad_luid_0x00000001_0x00000002_phys_0_eng_3_engtype_3D", 0, false)]
    public void ParsesOnlyUnambiguousCounterIdentities(string name, int counterKind, bool expected)
    {
        GpuCounterKind kind = (GpuCounterKind)counterKind;
        Assert.AreEqual(expected, PdhGpuCounterSource.TryParseIdentity(name, kind, out GpuCounterIdentity id));
        if (expected)
        {
            Assert.AreEqual(new GpuAdapterId(0x0000000100000002, 0), id.Adapter);
            if (kind != GpuCounterKind.AdapterMemory)
            {
                Assert.AreEqual(42, id.ProcessId);
            }
        }
    }

    private sealed class FakePdh : IPdhGpuApi
    {
        internal int Opens { get; private set; }
        internal int Adds { get; private set; }
        internal int Closes { get; private set; }
        internal uint AddStatus { get; set; }
        internal uint CollectStatus { get; set; }
        public uint Open(out nint query) { query = ++Opens; return 0; }
        public uint Add(nint query, string path, out nint counter) { counter = ++Adds; return AddStatus; }
        public uint Collect(nint query) => CollectStatus;
        public uint Read(nint counter, ref uint size, out uint count, nint buffer) { count = 0; return 0; }
        public void Close(nint query) => Closes++;
    }
}

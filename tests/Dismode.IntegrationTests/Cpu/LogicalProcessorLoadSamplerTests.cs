using Dismode.Windows.Cpu;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class LogicalProcessorLoadSamplerTests
{
    [TestMethod]
    public void StalledOrSlowCoreDoesNotRejectTheWholeWindow()
    {
        FakeProcessorTimes source = new();
        LogicalProcessorLoadSampler sampler = new(source);
        _ = sampler.Sample();
        source.Times[1] = new(0, 2_000_000, 0);
        Assert.AreEqual(100d, sampler.Sample()!.Value.CpuPercent);
        Assert.AreEqual(0d, sampler.ProcessorLoads[0]);
        source.Times[0] = new(0, 100, 0);
        source.Times[1] = new(0, 4_000_000, 0);
        Assert.IsNotNull(sampler.Sample());
    }

    [TestMethod]
    public void ExcessIdleIsClampedOnlyForTheLocalUiConsumer()
    {
        ProcessorTimes? times = new ProcessorTimes(0, 0, 0);
        SystemCpuLoadSampler strict = new(false, () => times);
        SystemCpuLoadSampler localUi = new(true, () => times);
        Assert.IsNull(strict.Sample());
        Assert.IsNull(localUi.Sample());
        times = new(3_000_000, 2_000_000, 0);
        Assert.IsNull(strict.Sample());
        Assert.AreEqual(0d, localUi.Sample());
        times = new(3_000_000, 1_000_000, 0);
        Assert.IsNull(strict.Sample());
        Assert.IsNull(localUi.Sample());
        times = null;
        Assert.IsNull(localUi.Sample());
    }

    [TestMethod]
    public void KernelIncludesIdleAndBusiestProcessorIsNotTheAverage()
    {
        FakeProcessorTimes source = new();
        LogicalProcessorLoadSampler sampler = new(source);
        Assert.IsNull(sampler.Sample());
        source.Times[0] = new(1_000_000, 2_000_000, 0);
        source.Times[1] = new(0, 1_000_000, 1_000_000);
        LogicalProcessorLoad load = sampler.Sample()!.Value;
        Assert.AreEqual(75d, load.CpuPercent);
        Assert.AreEqual(100d, load.BusiestProcessorPercent);
        Assert.AreEqual(2, sampler.ProcessorLoads.Length);
        Assert.AreEqual(50d, sampler.ProcessorLoads[0]);
        Assert.AreEqual(100d, sampler.ProcessorLoads[1]);
    }

    [TestMethod]
    public void RegressionAndImpossibleIdleRebaselineWithoutSpikes()
    {
        FakeProcessorTimes source = new();
        LogicalProcessorLoadSampler sampler = new(source);
        _ = sampler.Sample();
        source.Advance();
        Assert.IsNotNull(sampler.Sample());
        source.Times[0] = default;
        Assert.IsNull(sampler.Sample());
        source.Advance();
        Assert.IsNotNull(sampler.Sample());
        source.Times[0] = new(20_000_000, 4_000_000, 0);
        Assert.IsNull(sampler.Sample());
    }

    [TestMethod]
    public void ShortWindowAccumulatesAndFailureOrMultipleGroupsClearsBaseline()
    {
        FakeProcessorTimes source = new();
        LogicalProcessorLoadSampler sampler = new(source);
        _ = sampler.Sample();
        source.Times[0] = source.Times[1] = new(0, 100, 0);
        Assert.IsNull(sampler.Sample());
        source.Advance();
        Assert.IsNotNull(sampler.Sample());
        source.GroupCount = 2;
        int reads = source.Reads;
        Assert.IsNull(sampler.Sample());
        Assert.AreEqual(reads, source.Reads);
        source.GroupCount = 1;
        Assert.IsNull(sampler.Sample());
        source.Supported = false;
        Assert.IsNull(sampler.Sample());
        source.Supported = true;
        Assert.IsNull(sampler.Sample());
    }

    [TestMethod]
    public void ResetRequiresFreshBaseline()
    {
        FakeProcessorTimes source = new();
        LogicalProcessorLoadSampler sampler = new(source);
        _ = sampler.Sample();
        source.Advance();
        Assert.IsNotNull(sampler.Sample());
        sampler.Reset();
        Assert.IsNull(sampler.Sample());
    }

    internal sealed class FakeProcessorTimes(int count = 2) : ILogicalProcessorTimeSource
    {
        internal ProcessorTimes[] Times { get; } = new ProcessorTimes[count];
        public ushort GroupCount { get; set; } = 1;
        internal bool Supported { get; set; } = true;
        internal int Reads { get; private set; }
        internal int ThreadId { get; private set; }

        public bool TryRead(Span<ProcessorTimes> destination, out int count)
        {
            Reads++;
            ThreadId = Environment.CurrentManagedThreadId;
            Times.CopyTo(destination);
            count = Times.Length;
            return Supported;
        }

        internal void Advance()
        {
            for (int index = 0; index < Times.Length; index++)
            {
                ProcessorTimes last = Times[index];
                Times[index] = new(last.Idle + 1_000_000, last.Kernel + 2_000_000, last.User);
            }
        }
    }
}

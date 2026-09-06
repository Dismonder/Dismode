using GameShift.Core.Cpu;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Runs the CPU module against the machine the tests are running on. Nothing
/// here changes a process: the actuator records and does nothing, so the loop
/// can be exercised on a real inventory without touching anyone's priorities.
/// <para>
/// These assert on properties that must hold on any machine, not on numbers
/// from one particular CPU — the suite runs on both a uniform i7-11700F and a
/// hybrid Core Ultra 7 265K.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveCpuModuleTests
{
    [TestMethod]
    public void TopologyMatchesWhatTheRuntimeReports()
    {
        CpuTopology? topology = SystemCpuTopologyProvider.Read();

        Assert.IsNotNull(topology, "Nie odczytano topologii procesora.");
        Assert.AreEqual(
            Environment.ProcessorCount,
            topology.Processors.Count,
            "Liczba procesorow logicznych nie zgadza sie z runtime.");
        Assert.IsTrue(
            topology.Processors.All(processor => processor.Group == 0)
                || topology.Processors.Count > 64,
            "Grupa inna niz zerowa przy mniej niz 64 procesorach logicznych.");

        int physical = CpuTopology.CountPhysicalCores(topology.Processors);
        Assert.IsTrue(
            physical > 0 && physical <= topology.Processors.Count,
            $"Liczba rdzeni fizycznych poza zakresem: {physical}.");

        // Kazdy procesor logiczny musi dac sie zaadresowac w masce, inaczej
        // polityka policzylaby maske z dziurami.
        if (topology.Processors.Count <= 64)
        {
            ulong mask = CpuAffinityPolicy.BuildMask(topology.Processors);
            Assert.AreEqual(
                topology.Processors.Count,
                System.Numerics.BitOperations.PopCount(mask),
                "Maska nie obejmuje wszystkich procesorow logicznych.");
        }
    }

    [TestMethod]
    public void AffinityPolicyAgreesWithThisMachine()
    {
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            topology,
            CpuAffinityRole.Foreground);

        if (!topology.IsHybrid)
        {
            // Jednorodny procesor: polityka ma odmowic, a nie udawac zysk.
            Assert.IsFalse(decision.ShouldApply);
            Assert.AreEqual(
                CpuAffinityDecline.UniformTopology,
                decision.Decline);
            return;
        }

        // Hybrydowy: maska gry nie moze obejmowac zadnego wolnego rdzenia.
        Assert.IsTrue(decision.ShouldApply, decision.Explanation);
        ulong efficiency =
            CpuAffinityPolicy.BuildMask(topology.EfficiencyCores);
        Assert.AreEqual(
            0UL,
            decision.Mask & efficiency,
            "Maska gry zawiera rdzenie o nizszej klasie wydajnosci.");
    }

    [TestMethod]
    public void SystemLoadReadsSecondSampleOnwards()
    {
        SystemCpuLoadSampler sampler = new();

        Assert.IsNull(
            sampler.Sample(),
            "Pierwsza probka nie ma sie do czego odniesc.");

        Thread.Sleep(250);
        double? second = sampler.Sample();

        Assert.IsNotNull(second);
        Assert.IsTrue(
            second is >= 0 and <= 100,
            $"Obciazenie poza zakresem: {second}.");
    }

    [TestMethod]
    public async Task SupervisorMeasuresRealProcessesWithoutChangingThem()
    {
        NoOpActuator actuator = new();
        await using ProBalanceSupervisor supervisor = new(
            new ProcessInventory(),
            actuator,
            static () => new HashSet<int> { Environment.ProcessId },
            // Prog obciazenia podniesiony ponad maksimum, wiec silnik nigdy
            // nie uzna maszyny za obciazona i niczego nie ograniczy. Chodzi
            // o sprawdzenie pomiaru, nie o dzialanie na cudzych procesach.
            settings: new ProBalanceSettings { SystemLoadPercent = 200 });

        for (int index = 0; index < 3; index++)
        {
            IReadOnlyList<ProBalanceDecision> decisions =
                await supervisor.TickAsync(TestContext.CancellationTokenSource
                    .Token);
            Assert.AreEqual(
                0,
                decisions.Count,
                "Przy nieosiagalnym progu obciazenia nie moze zapasc zadna "
                    + "decyzja.");
            await Task.Delay(300);
        }

        Assert.AreEqual(0, actuator.Calls, "Aktuator zostal wywolany.");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class NoOpActuator : IProBalanceActuator
    {
        public int Calls { get; private set; }

        public ValueTask<bool> RestrainAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(false);
        }

        public ValueTask<bool> ReleaseAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(false);
        }
    }
}

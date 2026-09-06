using GameShift.Core.Cpu;

namespace GameShift.UnitTests.Cpu;

[TestClass]
public sealed class CpuAffinityPolicyTests
{
    /// <summary>
    /// Core Ultra 7 265K (Arrow Lake): 8 P-cores without hyper-threading and
    /// 12 E-cores, 20 logical processors in total. Windows reports the P tier
    /// with the higher efficiency class.
    /// </summary>
    private static CpuTopology ArrowLake()
    {
        List<CpuLogicalProcessor> processors = [];
        byte index = 0;
        for (uint core = 0; core < 8; core++, index++)
        {
            processors.Add(new(index, 0, index, core, 1, false, true));
        }

        for (uint core = 8; core < 20; core++, index++)
        {
            processors.Add(new(index, 0, index, core, 0, false, true));
        }

        return new(processors);
    }

    /// <summary>
    /// i7-11700F (Rocket Lake): 8 cores with hyper-threading, one tier.
    /// </summary>
    private static CpuTopology RocketLake()
    {
        List<CpuLogicalProcessor> processors = [];
        byte index = 0;
        for (uint core = 0; core < 16; core += 2)
        {
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
        }

        return new(processors);
    }

    [TestMethod]
    public void HybridTopologyIsRecognised()
    {
        CpuTopology arrow = ArrowLake();

        Assert.IsTrue(arrow.IsHybrid);
        Assert.AreEqual(20, arrow.Processors.Count);
        Assert.AreEqual(8, arrow.PerformanceCores.Count);
        Assert.AreEqual(12, arrow.EfficiencyCores.Count);
        Assert.AreEqual(
            8,
            CpuTopology.CountPhysicalCores(arrow.PerformanceCores));
    }

    [TestMethod]
    public void UniformTopologyIsRecognisedAndCountsCoresNotThreads()
    {
        CpuTopology rocket = RocketLake();

        Assert.IsFalse(rocket.IsHybrid);
        Assert.AreEqual(16, rocket.Processors.Count);
        // Osiem rdzeni, nie szesnascie: dwa procesory logiczne dziela rdzen.
        Assert.AreEqual(
            8,
            CpuTopology.CountPhysicalCores(rocket.Processors));
        Assert.AreEqual(0, rocket.EfficiencyCores.Count);
    }

    [TestMethod]
    public void GameIsPinnedToPerformanceCoresOnHybrid()
    {
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            ArrowLake(),
            CpuAffinityRole.Foreground);

        Assert.IsTrue(decision.ShouldApply);
        Assert.AreEqual(CpuAffinityDecline.None, decision.Decline);
        // Osiem najnizszych bitow: procesory logiczne 0-7.
        Assert.AreEqual(0x00FFUL, decision.Mask);
    }

    [TestMethod]
    public void BackgroundIsPushedToEfficiencyCoresOnHybrid()
    {
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            ArrowLake(),
            CpuAffinityRole.Background);

        Assert.IsTrue(decision.ShouldApply);
        // Procesory logiczne 8-19.
        Assert.AreEqual(0xFFF00UL, decision.Mask);
        Assert.AreEqual(0UL, decision.Mask & 0x00FFUL);
    }

    [TestMethod]
    public void UniformCpuIsLeftAlone()
    {
        // Na jednorodnym procesorze przypinanie niczego nie przenosi, a odbiera
        // harmonogramowi swobode. Polityka ma odmowic, nie udawac zysku.
        foreach (CpuAffinityRole role in Enum.GetValues<CpuAffinityRole>())
        {
            CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
                RocketLake(),
                role);

            Assert.IsFalse(decision.ShouldApply, role.ToString());
            Assert.AreEqual(
                CpuAffinityDecline.UniformTopology,
                decision.Decline);
            Assert.AreEqual(0UL, decision.Mask);
        }
    }

    [TestMethod]
    public void SmallPerformanceTierIsLeftAlone()
    {
        // Dwa szybkie rdzenie to za malo, zeby zamknac w nich gre.
        List<CpuLogicalProcessor> processors =
        [
            new(0, 0, 0, 0, 1, false, true),
            new(1, 0, 1, 1, 1, false, true),
            new(2, 0, 2, 2, 0, false, true),
            new(3, 0, 3, 3, 0, false, true),
            new(4, 0, 4, 4, 0, false, true),
            new(5, 0, 5, 5, 0, false, true),
        ];

        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            new(processors),
            CpuAffinityRole.Foreground);

        Assert.IsFalse(decision.ShouldApply);
        Assert.AreEqual(
            CpuAffinityDecline.PerformanceTierTooSmall,
            decision.Decline);
    }

    [TestMethod]
    public void MultipleProcessorGroupsAreRefused()
    {
        // Jedna 64-bitowa maska nie opisuje maszyny z wieloma grupami.
        // Obciecie jej przypielaby proces do niewlasciwej polowy komputera.
        List<CpuLogicalProcessor> processors =
            [.. ArrowLake().Processors, new(64, 1, 0, 32, 1, false, true)];

        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            new(processors),
            CpuAffinityRole.Foreground);

        Assert.IsFalse(decision.ShouldApply);
        Assert.AreEqual(
            CpuAffinityDecline.MultipleProcessorGroups,
            decision.Decline);
    }

    [TestMethod]
    public void MissingTopologyIsRefused()
    {
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            null,
            CpuAffinityRole.Foreground);

        Assert.IsFalse(decision.ShouldApply);
        Assert.AreEqual(
            CpuAffinityDecline.TopologyUnavailable,
            decision.Decline);
    }
}

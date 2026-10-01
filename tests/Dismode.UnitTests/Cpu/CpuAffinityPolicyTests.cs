using Dismode.Core.Cpu;

namespace Dismode.UnitTests.Cpu;

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
    public void UniformCpuLeavesTheGameAlone()
    {
        // Gry przypinac nie ma po co: rdzenie sa identyczne i dziela cache,
        // wiec przeniesienie jej gdziekolwiek niczego nie zmienia, a odbiera
        // harmonogramowi swobode.
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            RocketLake(),
            CpuAffinityRole.Foreground);

        Assert.IsFalse(decision.ShouldApply);
        Assert.AreEqual(
            CpuAffinityDecline.UniformTopology,
            decision.Decline);
        Assert.AreEqual(0UL, decision.Mask);
    }

    [TestMethod]
    public void UniformCpuStillConfinesBackground()
    {
        // Dla tla ta sama topologia znaczy cos innego i dlugo mialem to zle.
        // Nie chodzi o lokalnosc cache'u, tylko o odebranie rdzeni: proces
        // liczacy bez przerwy oddaje je grze dopiero wtedy, gdy nie wolno mu
        // ich dotknac. Zmierzone na takiej wlasnie maszynie: p99 czasu klatki
        // 16,80 ms przy wolnym tle, 10,65 ms po zamknieciu tla w cwiartce.
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            RocketLake(),
            CpuAffinityRole.Background);

        Assert.IsTrue(decision.ShouldApply, decision.Explanation);
        Assert.AreEqual(CpuAffinityDecline.None, decision.Decline);
        Assert.AreEqual(
            4,
            System.Numerics.BitOperations.PopCount(decision.Mask),
            "Cwiartka z szesnastu watkow to cztery.");
        Assert.AreEqual(
            0b1111UL,
            decision.Mask,
            "Maska ma obejmowac najnizsze procesory logiczne.");
    }

    /// <summary>
    /// i5-12400 albo Ryzen 5 5600: 6 rdzeni po dwa watki, 12 logicznych.
    /// Na takiej maszynie cwiartka puli wypada nieparzysto.
    /// </summary>
    private static CpuTopology SixCoresTwelveThreads()
    {
        List<CpuLogicalProcessor> processors = [];
        byte index = 0;
        for (uint core = 0; core < 6; core++)
        {
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
        }

        return new(processors);
    }

    [TestMethod]
    public void BackgroundCornerNeverSplitsAPhysicalCore()
    {
        // Na 12 watkach cwiartka to 3, a trzy watki to poltora rdzenia.
        // Wersja liczaca same procesory logiczne brala logiczne 0,1,2 i
        // zostawiala grze drugi watek rdzenia 1 — gra i tlo na jednym
        // rdzeniu fizycznym, czyli ta sama rywalizacja, ktorej ta maska ma
        // zapobiegac.
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            SixCoresTwelveThreads(),
            CpuAffinityRole.Background);

        Assert.IsTrue(decision.ShouldApply, decision.Explanation);

        CpuTopology topology = SixCoresTwelveThreads();
        foreach (IGrouping<uint, CpuLogicalProcessor> core in topology
            .Processors.GroupBy(processor => processor.CoreIndex))
        {
            int wTle = core.Count(processor =>
                (decision.Mask & (1UL << processor.LogicalProcessorIndex)) != 0);
            Assert.IsTrue(
                wTle == 0 || wTle == core.Count(),
                $"Rdzen {core.Key} jest rozdarty: {wTle} z "
                    + $"{core.Count()} watkow trafilo do tla. Rdzen ma byc "
                    + "zajety w calosci albo wcale.");
        }
    }

    [TestMethod]
    public void RocketLakeBackgroundCornerIsUnchanged()
    {
        // Ta maszyna dala pomiar p99 36,50 -> 14,34 ms. Zmiana na liczenie
        // calymi rdzeniami nie ma prawa ruszyc tego wyniku, bo cwiartka z
        // szesnastu watkow i tak wypada rowno na dwa rdzenie.
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            RocketLake(),
            CpuAffinityRole.Background);

        Assert.AreEqual(
            0b1111UL,
            decision.Mask,
            "Maska na Rocket Lake ma zostac dokladnie taka, jak byla.");
    }

    [TestMethod]
    public void SmallUniformCpuIsLeftAloneEvenForBackground()
    {
        // Na czterech watkach cwiartka to jeden watek, a odebranie calego tla
        // do jednego watku zamula powloke — wymiana jednego rodzaju przyciec
        // na inny.
        List<CpuLogicalProcessor> processors = [];
        byte index = 0;
        for (uint core = 0; core < 4; core += 2)
        {
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
            processors.Add(new(index, 0, index, core, 0, false, true));
            index++;
        }

        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            new CpuTopology(processors),
            CpuAffinityRole.Background);

        Assert.IsFalse(decision.ShouldApply);
        Assert.AreEqual(0UL, decision.Mask);
    }

    /// <summary>
    /// Ryzen 9 9950X: sixteen cores with SMT across two CCDs, every core the
    /// same efficiency class, but two separate last-level caches.
    /// </summary>
    private static CpuTopology DualCcdRyzen()
    {
        List<CpuLogicalProcessor> processors = [];
        byte index = 0;
        for (uint core = 0; core < 32; core += 2)
        {
            byte cache = (byte)(core < 16 ? 0 : 1);
            processors.Add(new(index, 0, index, core, 0, false, true, cache));
            index++;
            processors.Add(new(index, 0, index, core, 0, false, true, cache));
            index++;
        }

        return new(processors);
    }

    [TestMethod]
    public void SeparateCacheGroupsAreRecognisedOnAUniformCpu()
    {
        CpuTopology ryzen = DualCcdRyzen();

        // Klasa wydajnosci nic tu nie mowi — granica jest w cache.
        Assert.IsFalse(ryzen.IsHybrid);
        Assert.IsTrue(ryzen.HasSeparateCacheGroups);
        Assert.AreEqual(32, ryzen.Processors.Count);
        Assert.AreEqual(
            8,
            CpuTopology.CountPhysicalCores(ryzen.LargestCacheGroup));
    }

    [TestMethod]
    public void GameIsKeptInsideOneCacheGroup()
    {
        // Watki rozrzucone po obu CCD rozmawiaja przez pamiec zamiast przez
        // wspolny cache. Wczesniej polityka odmawiala tu calkowicie.
        CpuAffinityDecision game = CpuAffinityPolicy.Decide(
            DualCcdRyzen(),
            CpuAffinityRole.Foreground);
        CpuAffinityDecision background = CpuAffinityPolicy.Decide(
            DualCcdRyzen(),
            CpuAffinityRole.Background);

        Assert.IsTrue(game.ShouldApply, game.Explanation);
        Assert.IsTrue(background.ShouldApply, background.Explanation);
        Assert.AreEqual(0x0000FFFFUL, game.Mask);
        Assert.AreEqual(0xFFFF0000UL, background.Mask);
        Assert.AreEqual(0UL, game.Mask & background.Mask);
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

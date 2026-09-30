using Dismode.Core.Cpu;
using Dismode.Core.Domain.Processes;

namespace Dismode.UnitTests.Cpu;

[TestClass]
public sealed class ProBalanceEngineTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProcessRuntimeKey Hog =
        new(4242, new DateTimeOffset(2026, 9, 6, 11, 0, 0, TimeSpan.Zero));

    private static readonly ProcessRuntimeKey Game =
        new(1001, new DateTimeOffset(2026, 9, 6, 11, 0, 0, TimeSpan.Zero));

    private static ProBalanceObservation Busy(double cpu) =>
        new(Hog, "indexer", cpu, false, false);

    /// <summary>
    /// Feeds the engine the same reading repeatedly and returns every decision
    /// it produced, so a test can assert on what happened over a stretch of
    /// time rather than a single sample.
    /// </summary>
    private static List<ProBalanceDecision> Feed(
        ProBalanceEngine engine,
        int samples,
        double processCores,
        double backgroundCores,
        ref DateTimeOffset clock,
        TimeSpan? step = null)
    {
        TimeSpan interval = step ?? TimeSpan.FromSeconds(2);
        List<ProBalanceDecision> all = [];
        for (int index = 0; index < samples; index++)
        {
            all.AddRange(engine.Evaluate(
                [Busy(processCores)],
                backgroundCores,
                clock));
            clock += interval;
        }

        return all;
    }

    [TestMethod]
    public void SingleSpikeIsNotEnoughToRestrain()
    {
        // Jedna probka to szum. Reagowanie na nia dawaloby ciagle skoki
        // priorytetow, ktore kosztuja wiecej niz sam proces.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;

        List<ProBalanceDecision> decisions =
            Feed(engine, 2, 1.5, 4.0, ref clock);

        Assert.AreEqual(0, decisions.Count);
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void SustainedHogIsRestrained()
    {
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;

        List<ProBalanceDecision> decisions =
            Feed(engine, 3, 1.5, 4.0, ref clock);

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(ProBalanceAction.Restrain, decisions[0].Action);
        Assert.AreEqual("indexer", decisions[0].ProcessName);
        CollectionAssert.Contains(engine.Restrained.ToList(), Hog);
    }

    [TestMethod]
    public void IdleMachineIsLeftAlone()
    {
        // Bramka patrzy na to, ile zjada cale tlo, a nie cala maszyna —
        // praca samej gry nie jest dowodem, ze cos grze przeszkadza. Przy
        // cichym tle nie ma czego ograniczac: zmierzone na spoczywajacej
        // maszynie z gra, najbardziej zajety proces tla brał 0,07 rdzenia.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;

        List<ProBalanceDecision> decisions =
            Feed(engine, 10, 0.2, 0.2, ref clock);

        Assert.AreEqual(0, decisions.Count);
    }

    [TestMethod]
    public void GameAndProtectedProcessesAreNeverRestrained()
    {
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;

        for (int index = 0; index < 10; index++)
        {
            IReadOnlyList<ProBalanceDecision> decisions = engine.Evaluate(
                [
                    new(Game, "re9", 95, false, true),
                    new(new(7, Start), "dwm", 60, true, false),
                ],
                95,
                clock);
            Assert.AreEqual(0, decisions.Count);
            clock += TimeSpan.FromSeconds(2);
        }

        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void RestraintIsHeldThroughABriefDip()
    {
        // Proces zlapany w polowie zrywu czesto na chwile zwalnia, nie
        // konczac pracy. Zwolnienie go od razu oznaczaloby ponowne zlapanie
        // za dwie probki.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);

        List<ProBalanceDecision> decisions =
            Feed(engine, 1, 0.05, 4.0, ref clock);

        Assert.AreEqual(0, decisions.Count);
        CollectionAssert.Contains(engine.Restrained.ToList(), Hog);
    }

    [TestMethod]
    public void CalmProcessIsReleasedAfterTheMinimumHold()
    {
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);

        // Minimalne przytrzymanie to 5 s, wiec przy krokach 2 s pierwsze
        // spokojne probki jeszcze nie licza sie do zwolnienia.
        List<ProBalanceDecision> decisions =
            Feed(engine, 8, 0.05, 4.0, ref clock);

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(ProBalanceAction.Release, decisions[0].Action);
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void ReleasedProcessIsNotImmediatelyRecaught()
    {
        // To jest ten przypadek, ktory psuje naiwna implementacje: proces
        // wraca do 40% zaraz po zwolnieniu i zaczyna migotac miedzy stanami.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);
        Feed(engine, 8, 0.05, 4.0, ref clock);
        Assert.AreEqual(0, engine.Restrained.Count);

        List<ProBalanceDecision> decisions =
            Feed(engine, 10, 1.5, 4.0, ref clock);

        Assert.AreEqual(
            0,
            decisions.Count,
            "Proces zostal zlapany ponownie w okresie karencji.");
    }

    [TestMethod]
    public void RestraintIsAbandonedAfterTheMaximum()
    {
        // Proces trzymany od dwoch minut nie jest juz chwilowym zrywem.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);

        List<ProBalanceDecision> decisions = Feed(
            engine,
            30,
            1.5,
            90,
            ref clock,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(ProBalanceAction.Release, decisions[0].Action);
        StringAssert.Contains(decisions[0].Reason, "maksymalny");

        // Proces, ktory nigdy nie zwalnia, wchodzi w cykl: ograniczenie na
        // maksymalny czas, zwolnienie, karencja, ponowne zlapanie. Gra dostaje
        // wiekszosc korzysci, a tamta praca i tak posuwa sie do przodu.
        // Wazne jest, ze cykl jest rzadki — nie migotanie co probke.
        Assert.IsTrue(
            decisions.Count <= 4,
            $"Zbyt wiele przelaczen w 5 minut: {decisions.Count}.");
        for (int index = 0; index < decisions.Count; index++)
        {
            ProBalanceAction expected = index % 2 == 0
                ? ProBalanceAction.Release
                : ProBalanceAction.Restrain;
            Assert.AreEqual(expected, decisions[index].Action);
        }
    }

    [TestMethod]
    public void EndingTheSessionReleasesEverything()
    {
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);
        Assert.AreEqual(1, engine.Restrained.Count);

        IReadOnlyList<ProBalanceDecision> released = engine.ReleaseAll();

        Assert.AreEqual(1, released.Count);
        Assert.AreEqual(ProBalanceAction.Release, released[0].Action);
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void ProcessAdoptedByTheGameTreeIsReleased()
    {
        // Gra rozrasta drzewo i wciaga proces, ktory chwile wczesniej byl
        // zwyklym tlem i zdazyl zostac ograniczony. Samo pominiecie go
        // zostawiloby go na obnizonym priorytecie do konca sesji.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);
        Assert.AreEqual(1, engine.Restrained.Count);

        IReadOnlyList<ProBalanceDecision> decisions = engine.Evaluate(
            [new(Hog, "indexer", 1.5, false, true)],
            90,
            clock);

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(ProBalanceAction.Release, decisions[0].Action);
        StringAssert.Contains(decisions[0].Reason, "drzewa gry");
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void ProcessThatBecomesProtectedIsReleased()
    {
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);

        IReadOnlyList<ProBalanceDecision> decisions = engine.Evaluate(
            [new(Hog, "indexer", 1.5, true, false)],
            90,
            clock);

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(ProBalanceAction.Release, decisions[0].Action);
        StringAssert.Contains(decisions[0].Reason, "chroniony");
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void ProcessVanishingWhileRestrainedIsStillReleased()
    {
        // Znikniecie z inwentaryzacji zwykle znaczy koniec procesu, ale moze
        // tez znaczyc jedna nieudana probke. Bez wydania zwolnienia proces,
        // ktory nadal zyje, zostalby przy obnizonym priorytecie na zawsze.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);
        Assert.AreEqual(1, engine.Restrained.Count);

        IReadOnlyList<ProBalanceDecision> decisions =
            engine.Evaluate([], 90, clock);

        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(ProBalanceAction.Release, decisions[0].Action);
        Assert.AreEqual(Hog, decisions[0].RuntimeKey);
        Assert.AreEqual(0, engine.Restrained.Count);
    }

    [TestMethod]
    public void VanishedProcessDoesNotLeakState()
    {
        // Proces, ktory sie zakonczyl, znika z probki. Trzymanie jego stanu
        // w nieskonczonosc to wyciek w petli chodzacej cala sesje.
        ProBalanceEngine engine = new();
        DateTimeOffset clock = Start;
        Feed(engine, 3, 1.5, 4.0, ref clock);
        Assert.AreEqual(1, engine.Restrained.Count);

        engine.Evaluate([], 90, clock);

        Assert.AreEqual(0, engine.Restrained.Count);
    }
}

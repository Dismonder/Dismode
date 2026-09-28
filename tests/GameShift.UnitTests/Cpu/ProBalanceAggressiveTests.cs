using GameShift.Core.Cpu;
using GameShift.Core.Domain.Processes;

namespace GameShift.UnitTests.Cpu;

[TestClass]
public sealed class ProBalanceAggressiveTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private static ProcessRuntimeKey Key(int processId) =>
        new(processId, new DateTimeOffset(2026, 9, 28, 11, 0, 0, TimeSpan.Zero));

    private static ProBalanceObservation Background(int processId, double cpu) =>
        new(Key(processId), $"tlo-{processId}", cpu, false, false);

    [TestMethod]
    public void AggressivePresetUsesEveryLeverAndCoherentThresholds()
    {
        ProBalanceSettings settings = ProBalanceSettings.Aggressive;

        Assert.IsTrue(settings.ApplyEcoQos);
        Assert.IsTrue(settings.LowerBackgroundIoPriority);
        Assert.AreEqual(settings.RestrainAboveCores, settings.BackgroundLoadCores);
        Assert.IsLessThan(settings.RestrainAboveCores, settings.ReleaseBelowCores);
        Assert.IsLessThan(settings.CalmInterval, settings.BusyInterval);
        Assert.IsLessThan(
            new ProBalanceSettings().RestrainAboveCores,
            settings.RestrainAboveCores);
        Assert.IsLessThan(
            new ProBalanceSettings().SustainedSamples,
            settings.SustainedSamples);
        Assert.IsGreaterThan(
            new ProBalanceSettings().MaximumRestrained,
            settings.MaximumRestrained);
    }

    [TestMethod]
    public void AggressivePresetCatchesModerateHogAfterTwoSamples()
    {
        // 0,6 rdzenia jest ponizej ostroznego progu (0,75), wiec domyslne
        // ustawienia nigdy go nie ruszaja. Preset agresywny lapie go po
        // dwoch probkach.
        ProBalanceEngine cautious = new();
        ProBalanceEngine aggressive = new(ProBalanceSettings.Aggressive);
        DateTimeOffset clock = Start;
        List<ProBalanceDecision> cautiousDecisions = [];
        List<ProBalanceDecision> aggressiveDecisions = [];

        for (int sample = 0; sample < 2; sample++)
        {
            cautiousDecisions.AddRange(cautious.Evaluate(
                [Background(10, 0.6)],
                0.6,
                clock));
            aggressiveDecisions.AddRange(aggressive.Evaluate(
                [Background(10, 0.6)],
                0.6,
                clock));
            clock += ProBalanceSettings.Aggressive.BusyInterval;
        }

        Assert.IsEmpty(cautiousDecisions);
        Assert.HasCount(1, aggressiveDecisions);
        Assert.AreEqual(ProBalanceAction.Restrain, aggressiveDecisions[0].Action);
    }

    [TestMethod]
    public void AggressivePresetHoldsUpToEightProcessesAtOnce()
    {
        ProBalanceEngine engine = new(ProBalanceSettings.Aggressive);
        List<ProBalanceObservation> hogs =
            [.. Enumerable.Range(100, 10).Select(id => Background(id, 1.0))];
        DateTimeOffset clock = Start;

        for (int sample = 0; sample < 2; sample++)
        {
            _ = engine.Evaluate(hogs, 10.0, clock);
            clock += ProBalanceSettings.Aggressive.BusyInterval;
        }

        Assert.HasCount(8, engine.Restrained);
    }

    [TestMethod]
    public void CadenceSamplesFasterUnderLoadOrWhileRestraining()
    {
        ProBalanceSettings settings = ProBalanceSettings.Aggressive;

        Assert.AreEqual(
            settings.CalmInterval,
            ProBalanceCadence.NextInterval(settings, 0.1, 0));
        Assert.AreEqual(
            settings.BusyInterval,
            ProBalanceCadence.NextInterval(
                settings,
                settings.BackgroundLoadCores,
                0));
        Assert.AreEqual(
            settings.BusyInterval,
            ProBalanceCadence.NextInterval(settings, 0.0, 1));
    }

    [TestMethod]
    public void DefaultCadenceKeepsTheMeasuredOneSecondInterval()
    {
        ProBalanceSettings settings = new();

        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            ProBalanceCadence.NextInterval(settings, 0.0, 0));
        Assert.AreEqual(
            TimeSpan.FromSeconds(1),
            ProBalanceCadence.NextInterval(settings, 5.0, 3));
    }

    [TestMethod]
    public void CadenceRejectsNonPositiveInterval()
    {
        ProBalanceSettings broken = new() { BusyInterval = TimeSpan.Zero };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ProBalanceCadence.NextInterval(broken, 5.0, 0));
    }
}

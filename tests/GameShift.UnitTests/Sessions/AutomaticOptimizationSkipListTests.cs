using GameShift.Core.Sessions;

namespace GameShift.UnitTests.Sessions;

[TestClass]
public sealed class AutomaticOptimizationSkipListTests
{
    private static readonly DateTime StartedAtUtc =
        new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void SkippedInstanceStaysSkippedWhileItsStartTimeMatches()
    {
        AutomaticOptimizationSkipList list = new();

        list.Skip(4242, StartedAtUtc);

        Assert.IsTrue(list.IsSkipped(4242, StartedAtUtc));
        Assert.IsTrue(list.IsSkipped(
            4242,
            StartedAtUtc.ToLocalTime()),
            "Czas lokalny i UTC tej samej chwili to ten sam proces.");
        Assert.IsFalse(list.IsSkipped(4243, StartedAtUtc));
        Assert.AreEqual(1, list.Count);
    }

    [TestMethod]
    public void ReusedProcessIdWithDifferentStartTimeQualifiesAgain()
    {
        AutomaticOptimizationSkipList list = new();
        list.Skip(4242, StartedAtUtc);

        bool skipped = list.IsSkipped(
            4242,
            StartedAtUtc.AddMinutes(5));

        Assert.IsFalse(
            skipped,
            "Nowy proces pod starym PID to nowe uruchomienie gry.");
        Assert.AreEqual(
            0,
            list.Count,
            "Martwy wpis ma zniknac, zeby nie wrocil przy kolejnym pytaniu.");
    }

    [TestMethod]
    public void UnknownStartTimeOnEitherSideKeepsTheInstanceSkipped()
    {
        AutomaticOptimizationSkipList list = new();
        list.Skip(1, startedAtUtc: null);
        list.Skip(2, StartedAtUtc);

        Assert.IsTrue(list.IsSkipped(1, StartedAtUtc));
        Assert.IsTrue(list.IsSkipped(1, startedAtUtc: null));
        Assert.IsTrue(list.IsSkipped(2, startedAtUtc: null));
        Assert.AreEqual(2, list.Count);
    }

    [TestMethod]
    public void SkippingAgainReplacesTheStartTime()
    {
        AutomaticOptimizationSkipList list = new();
        list.Skip(7, StartedAtUtc);

        list.Skip(7, StartedAtUtc.AddHours(1));

        Assert.IsFalse(list.IsSkipped(7, StartedAtUtc));
        Assert.AreEqual(0, list.Count);
        list.Skip(7, StartedAtUtc.AddHours(1));
        Assert.IsTrue(list.IsSkipped(7, StartedAtUtc.AddHours(1)));
    }

    [TestMethod]
    public void ForgetExitedDropsOnlyProcessesMissingFromTheEnumeration()
    {
        AutomaticOptimizationSkipList list = new();
        list.Skip(10, StartedAtUtc);
        list.Skip(20, StartedAtUtc);
        list.Skip(30, startedAtUtc: null);

        int dropped = list.ForgetExited(new HashSet<int> { 20, 999 });

        Assert.AreEqual(2, dropped);
        Assert.IsFalse(list.IsSkipped(10, StartedAtUtc));
        Assert.IsTrue(list.IsSkipped(20, StartedAtUtc));
        Assert.IsFalse(list.IsSkipped(30, StartedAtUtc));
        Assert.AreEqual(0, list.ForgetExited(new HashSet<int> { 20 }));
    }

    [TestMethod]
    public void NonPositiveProcessIdIsRejected()
    {
        AutomaticOptimizationSkipList list = new();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => list.Skip(0, StartedAtUtc));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => list.Skip(-5, StartedAtUtc));
        Assert.AreEqual(0, list.Count);
    }
}

using Dismode.MemoryOptimizer.Core.Activity;
using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class GameActivityGuardTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void ActiveGameLeaseBlocksOptimization()
    {
        ActiveGameDocument active = new(
            1,
            "roblox",
            "Roblox",
            42,
            Now.AddMinutes(-1),
            Now.AddMinutes(1));

        GuardDecision result = GameActivityEvaluator.Evaluate(
            active,
            null,
            [new(42, "RobloxPlayerBeta", true, true)],
            Now);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("dismode-active-session", result.Code);
    }

    [TestMethod]
    public void KnownGameProcessBlocksWithoutActiveLease()
    {
        KnownGamesDocument known = new(
            1,
            Now,
            [new("dbd", "Dead by Daylight", ["DeadByDaylight-Win64-Shipping.exe"])]);

        GuardDecision result = GameActivityEvaluator.Evaluate(
            null,
            known,
            [new(7, "DeadByDaylight-Win64-Shipping", false, false)],
            Now);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("known-game-process", result.Code);
    }

    [TestMethod]
    public void ProtectedAntiCheatBlocksOptimization()
    {
        GuardDecision result = GameActivityEvaluator.Evaluate(
            null,
            null,
            [new(9, "EasyAntiCheat_EOS", false, false)],
            Now);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("protected-game-process", result.Code);
    }

    [TestMethod]
    public void UnknownFullscreenProcessBlocksOptimization()
    {
        GuardDecision result = GameActivityEvaluator.Evaluate(
            null,
            null,
            [new(11, "UnknownGame", true, true)],
            Now);

        Assert.IsTrue(result.IsBlocked);
        Assert.AreEqual("unknown-fullscreen-process", result.Code);
    }

    [TestMethod]
    public void ExpiredActiveLeaseDoesNotBlock()
    {
        ActiveGameDocument active = new(
            1,
            "old",
            "Old game",
            42,
            Now.AddMinutes(-5),
            Now.AddMinutes(-1));

        GuardDecision result = GameActivityEvaluator.Evaluate(
            active,
            null,
            [new(42, "ordinary", false, false)],
            Now);

        Assert.IsFalse(result.IsBlocked);
    }
}

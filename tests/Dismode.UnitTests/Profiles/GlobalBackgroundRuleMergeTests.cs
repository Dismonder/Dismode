using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Profiles;

namespace Dismode.UnitTests.Profiles;

[TestClass]
public sealed class GlobalBackgroundRuleMergeTests
{
    private static readonly string Root = Path.Combine(
        Path.GetTempPath(),
        "Dismode.GlobalRules");

    private static readonly string Chrome = Path.Combine(Root, "chrome.exe");
    private static readonly string Discord = Path.Combine(Root, "Discord.exe");
    private static readonly string Opera = Path.Combine(Root, "opera.exe");

    [TestMethod]
    public void PerGameRuleWinsOverGlobalRuleForTheSamePath()
    {
        GameOptimizationPreferences perGame = Preferences(
            new SavedBackgroundProcessRule(
                Chrome,
                SavedBackgroundActionMode.CloseAndRestore));

        GameOptimizationPreferences merged = perGame.WithGlobalRules(
        [
            new(Chrome, SavedBackgroundActionMode.LowerPriority),
            new(Discord, SavedBackgroundActionMode.RestrainBackground),
        ]);

        Assert.HasCount(2, merged.BackgroundRules);
        Assert.AreEqual(
            SavedBackgroundActionMode.CloseAndRestore,
            merged.BackgroundRules.Single(rule =>
                rule.ExecutablePath == Chrome).ActionMode);
        Assert.AreEqual(
            SavedBackgroundActionMode.RestrainBackground,
            merged.BackgroundRules.Single(rule =>
                rule.ExecutablePath == Discord).ActionMode);
    }

    [TestMethod]
    public void PerGameIgnoreKeepsTheGlobalRuleOut()
    {
        GameOptimizationPreferences perGame = Preferences(
            new SavedBackgroundProcessRule(
                Chrome,
                SavedBackgroundActionMode.Ignore));

        GameOptimizationPreferences merged = perGame.WithGlobalRules(
            [new(Chrome, SavedBackgroundActionMode.LowerPriority)]);

        Assert.HasCount(1, merged.BackgroundRules);
        Assert.AreEqual(
            SavedBackgroundActionMode.Ignore,
            merged.BackgroundRules[0].ActionMode);
    }

    [TestMethod]
    public void PathComparisonIgnoresCase()
    {
        GameOptimizationPreferences perGame = Preferences(
            new SavedBackgroundProcessRule(
                Chrome.ToLowerInvariant(),
                SavedBackgroundActionMode.LowerPriority));

        GameOptimizationPreferences merged = perGame.WithGlobalRules(
            [new(Chrome.ToUpperInvariant(), SavedBackgroundActionMode.CloseAndRestore)]);

        Assert.HasCount(1, merged.BackgroundRules);
        Assert.AreEqual(
            SavedBackgroundActionMode.LowerPriority,
            merged.BackgroundRules[0].ActionMode);
    }

    [TestMethod]
    public void NoGlobalRulesReturnsTheSameInstanceAndKeepsOtherFields()
    {
        GameOptimizationPreferences perGame = new(
            GameProfileId.Create(),
            SavedGamePriorityMode.High,
            [new(Opera, SavedBackgroundActionMode.CloseAndRestore)],
            DateTimeOffset.UtcNow,
            autoOptimizeWhenDetected: false);

        Assert.AreSame(perGame, perGame.WithGlobalRules([]));

        GameOptimizationPreferences merged = perGame.WithGlobalRules(
            [new(Discord, SavedBackgroundActionMode.LowerPriority)]);
        Assert.AreEqual(perGame.ProfileId, merged.ProfileId);
        Assert.AreEqual(SavedGamePriorityMode.High, merged.GamePriority);
        Assert.IsFalse(merged.AutoOptimizeWhenDetected);
        Assert.AreEqual(perGame.UpdatedAtUtc, merged.UpdatedAtUtc);
        Assert.HasCount(2, merged.BackgroundRules);
    }

    [TestMethod]
    public void GlobalRulesBeyondTheLimitAreDroppedInPathOrder()
    {
        GameOptimizationPreferences perGame = Preferences();
        List<SavedBackgroundProcessRule> globalRules = [];
        for (int index = 0; index < 130; index++)
        {
            globalRules.Add(new(
                Path.Combine(Root, $"app{index:000}.exe"),
                SavedBackgroundActionMode.LowerPriority));
        }

        GameOptimizationPreferences merged = perGame.WithGlobalRules(
            globalRules.AsEnumerable().Reverse());

        Assert.HasCount(128, merged.BackgroundRules);
        Assert.EndsWith("app000.exe", merged.BackgroundRules[0].ExecutablePath);
        Assert.EndsWith("app127.exe", merged.BackgroundRules[^1].ExecutablePath);
    }

    private static GameOptimizationPreferences Preferences(
        params SavedBackgroundProcessRule[] rules) =>
        new(
            GameProfileId.Create(),
            SavedGamePriorityMode.Normal,
            rules,
            DateTimeOffset.UtcNow);
}

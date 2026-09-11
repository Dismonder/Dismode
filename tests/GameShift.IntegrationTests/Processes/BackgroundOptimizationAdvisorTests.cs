using GameShift.Windows.Processes;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests.Processes;

[TestClass]
public sealed class BackgroundOptimizationAdvisorTests
{
    [TestMethod]
    public void RecommendsOptimizationForHighMemoryProcess()
    {
        bool recommend = BackgroundOptimizationAdvisor.ShouldRecommendOptimization(
            "CustomTool",
            workingSetBytes: 150L * 1024 * 1024,
            hasMainWindow: true,
            isAggressive: false);

        Assert.IsTrue(recommend);
    }

    [TestMethod]
    public void RecommendsOptimizationForKnownHeavyApps()
    {
        bool chromeRecommend = BackgroundOptimizationAdvisor.ShouldRecommendOptimization(
            "chrome",
            workingSetBytes: 20L * 1024 * 1024,
            hasMainWindow: true,
            isAggressive: false);

        bool discordRecommend = BackgroundOptimizationAdvisor.ShouldRecommendOptimization(
            "Discord",
            workingSetBytes: 30L * 1024 * 1024,
            hasMainWindow: true,
            isAggressive: false);

        Assert.IsTrue(chromeRecommend);
        Assert.IsTrue(discordRecommend);
    }

    [TestMethod]
    public void RecommendsCloseAndRestoreInAggressiveModeForWindowedApp()
    {
        BackgroundProcessActionMode action =
            BackgroundOptimizationAdvisor.RecommendAction(
                "chrome",
                hasMainWindow: true,
                canClose: true,
                isAggressive: true);

        Assert.AreEqual(BackgroundProcessActionMode.CloseAndRestore, action);
    }

    [TestMethod]
    public void RecommendsTheFullBundleInAggressiveModeWhenItCannotClose()
    {
        // Tryb agresywny obiecuje najsilniejsze odwracalne ograniczenia,
        // a jedyna dzwignia CPU ze zmierzonym zyskiem to twarda maska
        // cwiartki — czyli pelny pakiet, nie BelowNormal + EcoQoS.
        BackgroundProcessActionMode action =
            BackgroundOptimizationAdvisor.RecommendAction(
                "chrome",
                hasMainWindow: false,
                canClose: false,
                isAggressive: true);

        Assert.AreEqual(BackgroundProcessActionMode.RestrainBackground, action);
    }

    [TestMethod]
    public void RecommendsEcoQosInBalancedMode()
    {
        BackgroundProcessActionMode action =
            BackgroundOptimizationAdvisor.RecommendAction(
                "chrome",
                hasMainWindow: true,
                canClose: true,
                isAggressive: false);

        Assert.AreEqual(BackgroundProcessActionMode.LowerPriorityAndEcoQos, action);
    }

    [TestMethod]
    public void EstimatesPotentialMemorySavingsAccurately()
    {
        long workingSet = 500L * 1024 * 1024; // 500 MB

        long closeSavings = BackgroundOptimizationAdvisor.EstimatePotentialMemorySavings(
            workingSet,
            BackgroundProcessActionMode.CloseAndRestore);

        long ecoQosSavings = BackgroundOptimizationAdvisor.EstimatePotentialMemorySavings(
            workingSet,
            BackgroundProcessActionMode.LowerPriorityAndEcoQos);

        Assert.AreEqual(workingSet, closeSavings);
        Assert.IsTrue(ecoQosSavings > 0 && ecoQosSavings < workingSet);
    }
}

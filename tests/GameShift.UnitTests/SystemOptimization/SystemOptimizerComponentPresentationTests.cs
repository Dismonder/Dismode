using GameShift.UI.Services;

namespace GameShift.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerComponentPresentationTests
{
    [TestMethod]
    public void ActiveGameProfileIsVisibleFromTheMainApplication()
    {
        SystemOptimizerComponentSnapshot snapshot =
            SystemOptimizerComponentPresentation.CreateSnapshot(
                executableExists: true,
                serviceReachable: true,
                isReadOnly: false,
                isRecoveryClean: false,
                activeGameProfileId: "game-roblox",
                activeExperimentId: null,
                serviceMessage: "Profil działa.");

        Assert.AreEqual(SystemOptimizerComponentState.ActiveGameProfile, snapshot.State);
        Assert.AreEqual("Aktywny profil gry", snapshot.DisplayState);
        StringAssert.Contains(snapshot.Details, "game-roblox");
        Assert.IsTrue(snapshot.CanOpen);
    }

    [TestMethod]
    public void UnsignedClientIsClearlyReportedAsReadOnly()
    {
        SystemOptimizerComponentSnapshot snapshot =
            SystemOptimizerComponentPresentation.CreateSnapshot(
                executableExists: true,
                serviceReachable: true,
                isReadOnly: true,
                isRecoveryClean: true,
                activeGameProfileId: null,
                activeExperimentId: null,
                serviceMessage: "Brak zaufanego podpisu.");

        Assert.AreEqual(SystemOptimizerComponentState.ReadOnly, snapshot.State);
        Assert.AreEqual("Tylko odczyt", snapshot.DisplayState);
        StringAssert.Contains(snapshot.Details, "Brak zaufanego podpisu");
        Assert.IsTrue(snapshot.CanOpen);
    }
}

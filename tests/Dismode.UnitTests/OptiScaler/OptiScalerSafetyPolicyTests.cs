using Dismode.Core.OptiScaler;

namespace Dismode.UnitTests.OptiScaler;

[TestClass]
public sealed class OptiScalerSafetyPolicyTests
{
    [TestMethod]
    [DataRow(OptiScalerReleaseChannel.Stable, false)]
    [DataRow(OptiScalerReleaseChannel.Beta, true)]
    [DataRow(OptiScalerReleaseChannel.Nightly, true)]
    public void ReleaseChannelMarksExperimentalBuilds(
        OptiScalerReleaseChannel channel,
        bool expectedExperimental)
    {
        Assert.AreEqual(
            expectedExperimental,
            OptiScalerReleaseChannelPolicy.IsExperimental(channel));
    }

    [TestMethod]
    [DataRow("EasyAntiCheat_EOS.exe")]
    [DataRow(@"EasyAntiCheat\EasyAntiCheat.sys")]
    [DataRow(@"BattlEye\BEClient_x64.dll")]
    [DataRow("BEService_x64.exe")]
    [DataRow("vgk.sys")]
    [DataRow("RobloxPlayerBeta.exe")]
    public void KnownAntiCheatFilesBlockInstallation(string relativePath)
    {
        OptiScalerSafetyDecision decision = OptiScalerSafetyPolicy.Evaluate(
            gameIsRunning: false,
            offlineUseConfirmed: true,
            [relativePath]);

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.AntiCheatDetected,
            decision.BlockReason);
        Assert.AreEqual(relativePath, decision.Evidence);
    }

    [TestMethod]
    public void RunningGameBlocksInstallation()
    {
        OptiScalerSafetyDecision decision = OptiScalerSafetyPolicy.Evaluate(
            gameIsRunning: true,
            offlineUseConfirmed: true,
            []);

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GameRunning,
            decision.BlockReason);
    }

    [TestMethod]
    public void OfflineConfirmationIsRequiredForOtherwiseCleanGame()
    {
        OptiScalerSafetyDecision decision = OptiScalerSafetyPolicy.Evaluate(
            gameIsRunning: false,
            offlineUseConfirmed: false,
            []);

        Assert.IsFalse(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.OfflineUseNotConfirmed,
            decision.BlockReason);
    }

    [TestMethod]
    public void ConfirmedOfflineGameWithoutAntiCheatIsAllowed()
    {
        OptiScalerSafetyDecision decision = OptiScalerSafetyPolicy.Evaluate(
            gameIsRunning: false,
            offlineUseConfirmed: true,
            [@"Engine\Binaries\Win64\Game-Win64-Shipping.exe"]);

        Assert.IsTrue(decision.CanInstall);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.None,
            decision.BlockReason);
    }

    [TestMethod]
    [DataRow(OptiScalerProxy.Dxgi, "dxgi.dll")]
    [DataRow(OptiScalerProxy.Winmm, "winmm.dll")]
    [DataRow(OptiScalerProxy.Version, "version.dll")]
    [DataRow(OptiScalerProxy.D3d12, "d3d12.dll")]
    public void ProxyChoiceMapsOnlyToSupportedFileName(
        OptiScalerProxy proxy,
        string expectedFileName)
    {
        Assert.AreEqual(
            expectedFileName,
            OptiScalerSafetyPolicy.GetProxyFileName(proxy));
    }
}

using GameShift.Core.Activation;

namespace GameShift.UnitTests;

[TestClass]
public sealed class StartupPolicyTests
{
    private static readonly string OwnExecutable =
        Path.GetFullPath("GameShift.UI.exe");

    [TestMethod]
    public void LosingTheInstanceLockHandsOverBeforeAnythingElse()
    {
        StartupDecision decision = StartupPolicy.Decide(new(
            InstanceLockAcquired: false,
            HostExecutableExists: false,
            ForeignHostDirectory: @"C:\Elsewhere",
            OwnHostRunning: false));

        Assert.AreEqual(StartupDecisionKind.HandOver, decision.Kind);
        Assert.AreEqual(StartupPolicy.ExitHandedOver, decision.ExitCode);
        Assert.IsNull(decision.Message);
    }

    [TestMethod]
    public void MissingHostExecutableStopsWithAnExplanation()
    {
        StartupDecision decision = StartupPolicy.Decide(new(
            InstanceLockAcquired: true,
            HostExecutableExists: false,
            ForeignHostDirectory: null,
            OwnHostRunning: false));

        Assert.AreEqual(StartupDecisionKind.HostMissing, decision.Kind);
        Assert.AreEqual(StartupPolicy.ExitHostMissing, decision.ExitCode);
        Assert.IsNotNull(decision.Message);
        Assert.Contains("GameShift.SessionHost.exe", decision.Message);
    }

    [TestMethod]
    public void ForeignHostBlocksTheWindowAndNamesItsDirectory()
    {
        StartupDecision decision = StartupPolicy.Decide(new(
            InstanceLockAcquired: true,
            HostExecutableExists: true,
            ForeignHostDirectory: @"C:\Program Files\GameShift",
            OwnHostRunning: true));

        Assert.AreEqual(
            StartupDecisionKind.ForeignHostRunning,
            decision.Kind);
        Assert.AreEqual(StartupPolicy.ExitForeignHost, decision.ExitCode);
        Assert.IsNotNull(decision.Message);
        Assert.Contains(@"C:\Program Files\GameShift", decision.Message);
    }

    [TestMethod]
    public void OwnHostDecidesBetweenStartingAndContinuing()
    {
        StartupDecision start = StartupPolicy.Decide(new(
            InstanceLockAcquired: true,
            HostExecutableExists: true,
            ForeignHostDirectory: null,
            OwnHostRunning: false));
        StartupDecision proceed = StartupPolicy.Decide(new(
            InstanceLockAcquired: true,
            HostExecutableExists: true,
            ForeignHostDirectory: null,
            OwnHostRunning: true));

        Assert.AreEqual(StartupDecisionKind.StartHost, start.Kind);
        Assert.AreEqual(StartupDecisionKind.Continue, proceed.Kind);
        Assert.IsNull(proceed.Message);
    }

    [TestMethod]
    public void DeclinedElevationAndFailedStartHaveDistinctExitCodes()
    {
        StartupDecision declined = StartupPolicy.ElevationDeclined();
        StartupDecision failed = StartupPolicy.HostStartFailed("brak pliku");

        Assert.AreEqual(StartupPolicy.ExitElevationDeclined, declined.ExitCode);
        Assert.AreEqual(StartupPolicy.ExitHostStartFailed, failed.ExitCode);
        Assert.AreNotEqual(declined.ExitCode, failed.ExitCode);
        Assert.IsNotNull(declined.Message);
        Assert.IsNotNull(failed.Message);
        Assert.Contains("UAC", declined.Message);
        Assert.Contains("brak pliku", failed.Message);
        HashSet<int> codes =
        [
            StartupPolicy.ExitHandedOver,
            StartupPolicy.ExitHostMissing,
            StartupPolicy.ExitForeignHost,
            StartupPolicy.ExitElevationDeclined,
            StartupPolicy.ExitHostStartFailed,
        ];
        Assert.HasCount(5, codes, "Kody wyjscia maja byc rozne.");
    }

    [TestMethod]
    public void HandOverRequestLaunchesTheGameOrJustShowsTheWindow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string game = Path.GetFullPath("game.exe");

        UiActivationRequest launch = StartupPolicy.BuildHandOverRequest(
            new(StartInBackground: true, GameExecutablePath: game),
            OwnExecutable,
            now);
        UiActivationRequest show = StartupPolicy.BuildHandOverRequest(
            new(StartInBackground: false, GameExecutablePath: null),
            OwnExecutable,
            now);

        Assert.AreEqual(game, launch.GameExecutablePath);
        Assert.IsTrue(launch.KeepWindowHidden);
        Assert.IsFalse(launch.ShowOnly);
        Assert.AreEqual(OwnExecutable, show.GameExecutablePath);
        Assert.IsFalse(show.KeepWindowHidden);
        Assert.IsTrue(show.ShowOnly);
        Assert.AreNotEqual(launch.RequestId, show.RequestId);
    }

    [TestMethod]
    public void BackgroundStartWithoutAGameHandsOverSilently()
    {
        Assert.IsTrue(StartupPolicy.HandsOverSilently(
            new(StartInBackground: true, GameExecutablePath: null)));
        Assert.IsFalse(StartupPolicy.HandsOverSilently(
            new(StartInBackground: false, GameExecutablePath: null)));
        Assert.IsFalse(StartupPolicy.HandsOverSilently(
            new(
                StartInBackground: true,
                GameExecutablePath: Path.GetFullPath("game.exe"))));
    }
}

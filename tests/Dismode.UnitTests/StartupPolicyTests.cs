using Dismode.Core.Activation;

namespace Dismode.UnitTests;

[TestClass]
public sealed class StartupPolicyTests
{
    private static readonly string OwnExecutable =
        Path.GetFullPath("Dismode.UI.exe");

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
        Assert.Contains("Dismode.SessionHost.exe", decision.Message);
    }

    [TestMethod]
    public void ForeignHostBlocksTheWindowAndNamesItsDirectory()
    {
        StartupDecision decision = StartupPolicy.Decide(new(
            InstanceLockAcquired: true,
            HostExecutableExists: true,
            ForeignHostDirectory: @"C:\Program Files\Dismode",
            OwnHostRunning: true));

        Assert.AreEqual(
            StartupDecisionKind.ForeignHostRunning,
            decision.Kind);
        Assert.AreEqual(StartupPolicy.ExitForeignHost, decision.ExitCode);
        Assert.IsNotNull(decision.Message);
        Assert.Contains(@"C:\Program Files\Dismode", decision.Message);
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
            StartupPolicy.ExitLegacyProductRunning,
            StartupPolicy.ExitLegacyDataUnavailable,
        ];
        Assert.HasCount(7, codes, "Kody wyjscia maja byc rozne.");
    }

    [TestMethod]
    public void RunningLegacyProductStopsTheWindowAndNamesTheExecutable()
    {
        StartupDecision decision = StartupPolicy.LegacyProductRunning(
            @"C:\Program Files\GameShift\GameShift.UI.exe");

        Assert.AreEqual(
            StartupDecisionKind.ForeignHostRunning,
            decision.Kind);
        Assert.AreEqual(
            StartupPolicy.ExitLegacyProductRunning,
            decision.ExitCode);
        Assert.IsNotNull(decision.Message);
        Assert.Contains(
            @"C:\Program Files\GameShift\GameShift.UI.exe",
            decision.Message);
        Assert.Contains("GameShift", decision.Message);
    }

    [TestMethod]
    public void UnavailableLegacyDataStopsTheWindowAndListsTheProblems()
    {
        StartupDecision decision = StartupPolicy.LegacyDataUnavailable(
            [@"C:\Users\x\AppData\Local\GameShift\gameshift-user.db: in use"]);

        Assert.AreEqual(
            StartupPolicy.ExitLegacyDataUnavailable,
            decision.ExitCode);
        Assert.IsNotNull(decision.Message);
        Assert.Contains("gameshift-user.db: in use", decision.Message);
        Assert.Contains("pustej biblioteki", decision.Message);
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

    [TestMethod]
    public void AHostThatRefusedToServeIsReportedLikeBlockedMigration()
    {
        StartupDecision refused =
            StartupPolicy.HostRefused(StartupPolicy.HostExitRefused);
        StartupDecision crashed = StartupPolicy.HostRefused(1);

        Assert.AreEqual(StartupPolicy.ExitLegacyDataUnavailable, refused.ExitCode);
        Assert.IsNotNull(refused.Message);
        Assert.Contains("GameShift", refused.Message);
        Assert.AreEqual(StartupPolicy.ExitHostStartFailed, crashed.ExitCode);
        Assert.IsNotNull(crashed.Message);
        Assert.Contains("kodem 1", crashed.Message);
    }
}

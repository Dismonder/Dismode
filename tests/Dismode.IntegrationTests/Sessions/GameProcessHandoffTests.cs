using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

[TestClass]
public sealed class GameProcessHandoffTests
{
    [TestMethod]
    public async Task InstanceAlreadyRunningAtActivationIsNeverAdopted()
    {
        await using SyntheticSession session = new();
        ProcessIdentity unrelated = new(new(12346, session.Clock.GetUtcNow().AddSeconds(-5)),
            session.Root.ExecutablePath, session.Root.ExecutableSha256, null, "user", 1, null);
        session.Running.Add(unrelated);
        await session.StartAsync();
        session.Running.Clear();
        session.Running.Add(unrelated);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        session.Clock.Advance(30);
        await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
        Assert.IsFalse((await session.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffAdopted));
    }

    [TestMethod]
    public void ReplacementBornBeforeStubExitIsAcceptedButActivationBaselineIsRejected()
    {
        ProcessIdentity root = Identity(1, MissingAt.AddSeconds(-10));
        ProcessIdentity replacement = Identity(2, MissingAt.AddSeconds(-3));
        GameHandoffIdentity approval = new(root.ExecutablePath, root.ExecutableSha256, []);
        Assert.AreSame(replacement, new GameProcessHandoff(MissingAt, 0)
            .Select(root, [root], [replacement], TimeSpan.Zero, MissingAt, approval));
        approval = approval with { ActivationBaseline = [replacement.RuntimeKey] };
        Assert.IsNull(new GameProcessHandoff(MissingAt, 0)
            .Select(root, [root], [replacement], TimeSpan.Zero, MissingAt, approval));
    }

    [TestMethod]
    public async Task NonDescendantStartedBeforeStubExitUsesProfileIdentityAndItsOwnHash()
    {
        const string gamePath = @"C:\Games\Actual\game.exe";
        string hash = new('B', 64);
        await using SyntheticSession session = new(profileExecutablePath: gamePath,
            profileExecutableSha256: hash, rootExecutablePath: @"C:\Games\Stub\stub.exe");
        await session.StartAsync();
        session.Clock.Advance(1);
        ProcessIdentity game = new(new(12346, session.Clock.GetUtcNow()), gamePath, hash, null, "user", 1, null);
        session.Running.Add(game);
        session.Clock.Advance(3);
        session.Running.Clear();
        session.Running.Add(game);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffAdopted);
        var entries = await session.Journal.ReadAllAsync(CancellationToken.None);
        StringAssert.Contains(entries.Last(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffAdopted).Details!, hash);
        Assert.IsNotNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task RefusedTrackerAdoptionRestoresAndDoesNotJournalSuccessfulAdoption()
    {
        await using SyntheticSession session = new(refuseAdoption: true);
        await session.StartAsync();
        session.Clock.Advance(1);
        session.Running.Clear();
        session.Running.Add(new(new(12346, session.Clock.GetUtcNow()), session.Root.ExecutablePath,
            session.Root.ExecutableSha256, null, "user", 1, null));
        await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
        var entries = await session.Journal.ReadAllAsync(CancellationToken.None);
        Assert.IsTrue(entries.Any(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffRefused));
        Assert.IsFalse(entries.Any(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffAdopted));
    }

    [TestMethod]
    public async Task ShutdownReservationRestoresImmediatelyDuringHandoffWait()
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        session.Running.Clear();
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        var readiness = await session.Orchestrator.ReserveShutdownAsync(CancellationToken.None);
        Assert.IsTrue(readiness.CanShutdown);
        Assert.IsNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitRestoreAndAmbiguousHandoffRestoreWithoutWaitingForTimeout(bool ambiguous)
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        session.Running.Clear();
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        if (ambiguous)
        {
            session.Clock.Advance(1);
            session.AddCandidates(HandoffCandidatePids.Select(pid => new ProcessIdentity(
                new(pid, session.Clock.GetUtcNow()), session.Root.ExecutablePath,
                session.Root.ExecutableSha256, null, "user", 1, null)).ToArray());
            await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
        }
        else
        {
            await session.Orchestrator.RestoreAsync(session.Id, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.IsNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
        Assert.IsFalse((await session.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffAdopted));
    }

    [TestMethod]
    public async Task MonitorJournalsAdoptionAfterFourIntervals()
    {
        await using SyntheticSession session = new();
        await session.StartAsync();
        session.Running.Clear();
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        await Task.Delay(40);
        session.Clock.Advance(4);
        ProcessIdentity replacement = new(new(12346, session.Clock.GetUtcNow()),
            session.Root.ExecutablePath, session.Root.ExecutableSha256, null, "user", 1, null);
        session.Running.Add(replacement);
        await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffAdopted);
        var entries = await session.Journal.ReadAllAsync(CancellationToken.None);
        StringAssert.Contains(entries.Last(entry => entry.SessionCheckpoint == SessionCheckpoint.GameHandoffAdopted).Details!, "12346");
        Assert.IsNotNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task MonitorTimeoutRestoresAndShutdownDuringWaitRestoresImmediately()
    {
        await using (SyntheticSession session = new())
        {
            await session.StartAsync();
            session.Running.Clear();
            await session.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
            session.Clock.Advance(30);
            await session.WaitForCheckpointAsync(SessionCheckpoint.ReconciliationComplete);
            Assert.IsNull(await session.Orchestrator.GetActiveAsync(CancellationToken.None));
        }

        await using SyntheticSession cancelled = new();
        await cancelled.StartAsync();
        cancelled.Running.Clear();
        await cancelled.WaitForCheckpointAsync(SessionCheckpoint.GameHandoffWaiting);
        await cancelled.Orchestrator.DisposeAsync();
        Assert.IsTrue((await cancelled.Journal.ReadAllAsync(CancellationToken.None))
            .Any(entry => entry.SessionCheckpoint == SessionCheckpoint.ReconciliationComplete));
    }

    private static readonly DateTimeOffset MissingAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
    private static readonly int[] HandoffCandidatePids = [12346, 12347];

    [TestMethod]
    public void ReplacementAfterFourIntervalsIsAdopted()
    {
        GameProcessHandoff handoff = new(MissingAt, 0);
        ProcessIdentity root = Identity(1, MissingAt.AddSeconds(-10));
        for (int second = 0; second < 4; second++)
        {
            Assert.IsNull(handoff.Select(root, [root], [], TimeSpan.FromSeconds(second)));
            Assert.IsFalse(handoff.Finished);
        }

        ProcessIdentity replacement = Identity(2, MissingAt.AddSeconds(4));
        Assert.AreSame(replacement, handoff.Select(root, [root], [replacement], TimeSpan.FromSeconds(4)));
    }

    [TestMethod]
    public void TimeoutAndAmbiguityRefuseAdoption()
    {
        ProcessIdentity root = Identity(1, MissingAt.AddSeconds(-10));
        GameProcessHandoff timeout = new(MissingAt, 0);
        Assert.IsNull(timeout.Select(root, [root], [], TimeSpan.FromSeconds(30)));
        Assert.IsTrue(timeout.Finished);
        GameProcessHandoff ambiguous = new(MissingAt, 0);
        Assert.IsNull(ambiguous.Select(root, [root],
            [Identity(2, MissingAt), Identity(3, MissingAt)], TimeSpan.Zero));
        Assert.IsTrue(ambiguous.Finished);
    }

    [TestMethod]
    public void RejectsWrongExecutableUserSessionOldProcessAndReusedPid()
    {
        ProcessIdentity root = Identity(1, MissingAt.AddSeconds(-10));
        ProcessIdentity[] rejected =
        [
            Identity(2, MissingAt, path: @"C:\other.exe"),
            Identity(2, MissingAt, hash: new string('B', 64)),
            Identity(2, MissingAt, user: "other"),
            Identity(2, MissingAt, session: 2),
            Identity(2, root.RuntimeKey.StartedAtUtc.AddSeconds(-1)),
            Identity(1, MissingAt),
            Identity(2, MissingAt.AddSeconds(31)),
        ];
        foreach (ProcessIdentity candidate in rejected)
        {
            GameProcessHandoff handoff = new(MissingAt, 0);
            Assert.IsNull(handoff.Select(root, [root], [candidate], TimeSpan.FromSeconds(1)));
            Assert.IsFalse(handoff.Finished);
        }
    }

    private static ProcessIdentity Identity(int pid, DateTimeOffset started,
        string path = @"C:\game.exe", string? hash = null, string user = "user", int session = 1) =>
        new(new(pid, started), path, hash ?? new string('A', 64), null, user, session, null);
}

using Dismode.Core.Sessions;

namespace Dismode.UnitTests.Sessions;

[TestClass]
public sealed class SessionStateMachineTests
{
    private static readonly HashSet<(OptimizationSessionState From, OptimizationSessionState To)>
        ExpectedTransitions =
        [
            (OptimizationSessionState.Idle, OptimizationSessionState.Discovering),
            (OptimizationSessionState.Discovering, OptimizationSessionState.Profiling),
            (OptimizationSessionState.Profiling, OptimizationSessionState.Planning),
            (OptimizationSessionState.Planning, OptimizationSessionState.AwaitingApproval),
            (OptimizationSessionState.AwaitingApproval, OptimizationSessionState.Preflight),
            (OptimizationSessionState.AwaitingApproval, OptimizationSessionState.Idle),
            (OptimizationSessionState.Preflight, OptimizationSessionState.Snapshotting),
            (OptimizationSessionState.Preflight, OptimizationSessionState.AwaitingApproval),
            (OptimizationSessionState.Preflight, OptimizationSessionState.Idle),
            (OptimizationSessionState.Snapshotting, OptimizationSessionState.LaunchingGame),
            (OptimizationSessionState.Snapshotting, OptimizationSessionState.RecoveryRequired),
            (OptimizationSessionState.Applying, OptimizationSessionState.Verifying),
            (OptimizationSessionState.Applying, OptimizationSessionState.Restoring),
            (OptimizationSessionState.Applying, OptimizationSessionState.RecoveryRequired),
            (OptimizationSessionState.Verifying, OptimizationSessionState.Active),
            (OptimizationSessionState.Verifying, OptimizationSessionState.Restoring),
            (OptimizationSessionState.Verifying, OptimizationSessionState.RecoveryRequired),
            (OptimizationSessionState.LaunchingGame, OptimizationSessionState.Applying),
            (OptimizationSessionState.LaunchingGame, OptimizationSessionState.GameCrashed),
            (OptimizationSessionState.LaunchingGame, OptimizationSessionState.Restoring),
            (OptimizationSessionState.Active, OptimizationSessionState.Degraded),
            (OptimizationSessionState.Active, OptimizationSessionState.GameCrashed),
            (OptimizationSessionState.Active, OptimizationSessionState.UserRestoreRequested),
            (OptimizationSessionState.Active, OptimizationSessionState.GameExited),
            (OptimizationSessionState.Degraded, OptimizationSessionState.Active),
            (OptimizationSessionState.Degraded, OptimizationSessionState.GameCrashed),
            (OptimizationSessionState.Degraded, OptimizationSessionState.UserRestoreRequested),
            (OptimizationSessionState.Degraded, OptimizationSessionState.GameExited),
            (OptimizationSessionState.Degraded, OptimizationSessionState.Restoring),
            (OptimizationSessionState.GameCrashed, OptimizationSessionState.Restoring),
            (OptimizationSessionState.UserRestoreRequested, OptimizationSessionState.Restoring),
            (OptimizationSessionState.GameExited, OptimizationSessionState.Restoring),
            (OptimizationSessionState.Restoring, OptimizationSessionState.Reconciling),
            (OptimizationSessionState.Restoring, OptimizationSessionState.RecoveryRequired),
            (OptimizationSessionState.Reconciling, OptimizationSessionState.Completed),
            (OptimizationSessionState.Reconciling, OptimizationSessionState.CompletedWithWarnings),
            (OptimizationSessionState.Reconciling, OptimizationSessionState.RecoveryRequired),
            (OptimizationSessionState.Completed, OptimizationSessionState.Idle),
            (OptimizationSessionState.CompletedWithWarnings, OptimizationSessionState.Idle),
            (OptimizationSessionState.RecoveryRequired, OptimizationSessionState.Restoring),
        ];

    [TestMethod]
    public void TransitionMatrixAllowsExactlyTheSpecifiedEdges()
    {
        OptimizationSessionState[] states = Enum.GetValues<OptimizationSessionState>();

        foreach (OptimizationSessionState from in states)
        {
            foreach (OptimizationSessionState to in states)
            {
                bool expected = ExpectedTransitions.Contains((from, to));
                bool actual = SessionStateMachine.CanTransition(from, to);

                Assert.AreEqual(
                    expected,
                    actual,
                    $"Unexpected transition decision for {from} -> {to}.");
            }
        }
    }

    [TestMethod]
    public void HappyPathCannotSkipSafetyStages()
    {
        OptimizationSessionState state = OptimizationSessionState.Idle;
        OptimizationSessionState[] path =
        [
            OptimizationSessionState.Discovering,
            OptimizationSessionState.Profiling,
            OptimizationSessionState.Planning,
            OptimizationSessionState.AwaitingApproval,
            OptimizationSessionState.Preflight,
            OptimizationSessionState.Snapshotting,
            OptimizationSessionState.LaunchingGame,
            OptimizationSessionState.Applying,
            OptimizationSessionState.Verifying,
            OptimizationSessionState.Active,
            OptimizationSessionState.GameExited,
            OptimizationSessionState.Restoring,
            OptimizationSessionState.Reconciling,
            OptimizationSessionState.Completed,
            OptimizationSessionState.Idle,
        ];

        foreach (OptimizationSessionState next in path)
        {
            state = SessionStateMachine.Transition(state, next);
        }

        Assert.AreEqual(OptimizationSessionState.Idle, state);
    }

    [TestMethod]
    public void ApplyingCannotSkipVerificationAndLaunchGame()
    {
        Assert.IsFalse(
            SessionStateMachine.CanTransition(
                OptimizationSessionState.Applying,
                OptimizationSessionState.Active));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => SessionStateMachine.Transition(
                OptimizationSessionState.Applying,
                OptimizationSessionState.Active));
    }

    [TestMethod]
    public void FailureBranchesAlwaysLeadThroughRestore()
    {
        OptimizationSessionState[] failureStates =
        [
            OptimizationSessionState.GameCrashed,
            OptimizationSessionState.UserRestoreRequested,
            OptimizationSessionState.GameExited,
            OptimizationSessionState.RecoveryRequired,
        ];

        foreach (OptimizationSessionState failureState in failureStates)
        {
            Assert.IsTrue(
                SessionStateMachine.CanTransition(
                    failureState,
                    OptimizationSessionState.Restoring),
                $"{failureState} must lead to Restoring.");
        }
    }
}

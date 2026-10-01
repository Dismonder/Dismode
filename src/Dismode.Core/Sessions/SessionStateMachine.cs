namespace Dismode.Core.Sessions;

public static class SessionStateMachine
{
    public static bool CanTransition(
        OptimizationSessionState current,
        OptimizationSessionState next) =>
        current switch
        {
            OptimizationSessionState.Idle =>
                next is OptimizationSessionState.Discovering,
            OptimizationSessionState.Discovering =>
                next is OptimizationSessionState.Profiling,
            OptimizationSessionState.Profiling =>
                next is OptimizationSessionState.Planning,
            OptimizationSessionState.Planning =>
                next is OptimizationSessionState.AwaitingApproval,
            OptimizationSessionState.AwaitingApproval =>
                next is OptimizationSessionState.Preflight or OptimizationSessionState.Idle,
            OptimizationSessionState.Preflight =>
                next is OptimizationSessionState.Snapshotting
                    or OptimizationSessionState.AwaitingApproval
                    or OptimizationSessionState.Idle,
            OptimizationSessionState.Snapshotting =>
                next is OptimizationSessionState.LaunchingGame
                    or OptimizationSessionState.RecoveryRequired,
            OptimizationSessionState.Applying =>
                next is OptimizationSessionState.Verifying
                    or OptimizationSessionState.Restoring
                    or OptimizationSessionState.RecoveryRequired,
            OptimizationSessionState.Verifying =>
                next is OptimizationSessionState.Active
                    or OptimizationSessionState.Restoring
                    or OptimizationSessionState.RecoveryRequired,
            OptimizationSessionState.LaunchingGame =>
                next is OptimizationSessionState.Applying
                    or OptimizationSessionState.GameCrashed
                    or OptimizationSessionState.Restoring,
            OptimizationSessionState.Active =>
                next is OptimizationSessionState.Degraded
                    or OptimizationSessionState.GameCrashed
                    or OptimizationSessionState.UserRestoreRequested
                    or OptimizationSessionState.GameExited,
            OptimizationSessionState.Degraded =>
                next is OptimizationSessionState.Active
                    or OptimizationSessionState.GameCrashed
                    or OptimizationSessionState.UserRestoreRequested
                    or OptimizationSessionState.GameExited
                    or OptimizationSessionState.Restoring,
            OptimizationSessionState.GameCrashed
                or OptimizationSessionState.UserRestoreRequested
                or OptimizationSessionState.GameExited =>
                    next is OptimizationSessionState.Restoring,
            OptimizationSessionState.Restoring =>
                next is OptimizationSessionState.Reconciling
                    or OptimizationSessionState.RecoveryRequired,
            OptimizationSessionState.Reconciling =>
                next is OptimizationSessionState.Completed
                    or OptimizationSessionState.CompletedWithWarnings
                    or OptimizationSessionState.RecoveryRequired,
            OptimizationSessionState.Completed
                or OptimizationSessionState.CompletedWithWarnings =>
                    next is OptimizationSessionState.Idle,
            OptimizationSessionState.RecoveryRequired =>
                next is OptimizationSessionState.Restoring,
            _ => false,
        };

    public static OptimizationSessionState Transition(
        OptimizationSessionState current,
        OptimizationSessionState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException(
                $"Session transition from {current} to {next} is not allowed.");
        }

        return next;
    }
}

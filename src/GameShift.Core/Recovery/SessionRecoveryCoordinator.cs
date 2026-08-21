using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Recovery;

public static class SessionRecoveryCoordinator
{
    public static async ValueTask<SessionRecoveryResult> RecoverInReverseApplicationOrderAsync(
        IReadOnlyList<IRecoveryOperation> appliedActions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appliedActions);

        HashSet<ActionId> actionIds = [];
        foreach (IRecoveryOperation action in appliedActions)
        {
            if (!actionIds.Add(action.ActionId))
            {
                throw new ArgumentException(
                    $"Action {action.ActionId} appears more than once in the recovery plan.",
                    nameof(appliedActions));
            }
        }

        List<SessionActionRecovery> results = new(appliedActions.Count);

        for (int index = appliedActions.Count - 1; index >= 0; index--)
        {
            IRecoveryOperation action = appliedActions[index];
            ActionRecoveryResult result =
                await action.RecoverAsync(cancellationToken).ConfigureAwait(false);
            results.Add(new(action.ActionId, result));
        }

        return new(results);
    }
}

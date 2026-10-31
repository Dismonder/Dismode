using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Recovery;
using Dismode.Windows.Cpu;

namespace Dismode.Windows.Processes;

public sealed record ProcessCpuSetsTransaction(Guid SessionId, Guid ActionId, Guid IdempotencyKey)
{
    internal static ProcessCpuSetsTransaction Create(SessionId sessionId) =>
        new(sessionId.Value, Guid.NewGuid(), Guid.NewGuid());

    internal ActionExecutionContext Context => new(new(SessionId), new(ActionId),
        new(IdempotencyKey), DateTimeOffset.UtcNow);

    internal async ValueTask<ActionRecoveryResult> RecoverAsync(IRecoveryJournal journal,
        ProcessIdentity identity, IProcessIdentityProvider identities, ICpuSetsPort port,
        CancellationToken cancellationToken) =>
        await new ActionRecoveryCoordinator<ProcessCpuSetsState>(journal)
            .RecoverAsync(new ProcessCpuSetsAction(new(ActionId), identity, [], identities, port),
                Context, cancellationToken).ConfigureAwait(false);
}

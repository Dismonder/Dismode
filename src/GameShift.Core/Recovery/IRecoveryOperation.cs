using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Recovery;

public interface IRecoveryOperation
{
    ActionId ActionId { get; }

    ValueTask<ActionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken);
}


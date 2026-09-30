using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Recovery;

public interface IRecoveryOperation
{
    ActionId ActionId { get; }

    ValueTask<ActionRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken);
}


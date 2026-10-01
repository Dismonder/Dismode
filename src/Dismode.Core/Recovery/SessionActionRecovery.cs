using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Recovery;

public sealed record SessionActionRecovery(
    ActionId ActionId,
    ActionRecoveryResult Result);


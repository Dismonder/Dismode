using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Recovery;

public sealed record SessionActionRecovery(
    ActionId ActionId,
    ActionRecoveryResult Result);


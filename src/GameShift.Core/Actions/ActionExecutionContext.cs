using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Actions;

public sealed record ActionExecutionContext(
    SessionId SessionId,
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset RequestedAtUtc)
{
    public DateTimeOffset RequestedAtUtc { get; init; } = RequestedAtUtc.ToUniversalTime();
}


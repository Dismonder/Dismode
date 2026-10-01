using Dismode.Contracts.Protocol;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Actions;

public sealed record ActionExecutionContext(
    SessionId SessionId,
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    DateTimeOffset RequestedAtUtc)
{
    public DateTimeOffset RequestedAtUtc { get; init; } = RequestedAtUtc.ToUniversalTime();
}


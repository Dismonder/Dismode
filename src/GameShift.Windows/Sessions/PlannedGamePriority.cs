using System.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;

namespace GameShift.Windows.Sessions;

public sealed record PlannedGamePriority(
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    ProcessPriorityClass DesiredPriority);

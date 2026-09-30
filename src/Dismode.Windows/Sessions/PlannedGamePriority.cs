using System.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.Windows.Sessions;

public sealed record PlannedGamePriority(
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    ProcessPriorityClass DesiredPriority);

using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

public sealed record PlannedBackgroundApplication(
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    string DisplayName,
    ProcessIdentity Identity,
    BackgroundProcessActionMode ActionMode,
    ActionId? EcoQosActionId,
    IdempotencyKey? EcoQosIdempotencyKey,
    ActionId? AffinityActionId,
    IdempotencyKey? AffinityIdempotencyKey,
    ActionId? IoPriorityActionId,
    IdempotencyKey? IoPriorityIdempotencyKey,
    ActionId? MemoryPriorityActionId,
    IdempotencyKey? MemoryPriorityIdempotencyKey,
    ApplicationRestartDescriptor? RestartDescriptor,
    long EstimatedWorkingSetBytes);

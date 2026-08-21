using GameShift.Core.Actions;
using GameShift.Core.Devices;

namespace GameShift.Core.Policies;

public sealed record SafetyEvaluationContext
{
    public SafetyEvaluationContext(
        string targetId,
        OptimizationActionKind actionKind,
        TargetProtection protection,
        RecoveryAssurance recoveryAssurance,
        bool isApprovedTarget,
        bool isOwnedByGameShift,
        DeviceFormFactor formFactor = DeviceFormFactor.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        TargetId = targetId.Trim();
        ActionKind = actionKind;
        Protection = protection;
        RecoveryAssurance = recoveryAssurance;
        IsApprovedTarget = isApprovedTarget;
        IsOwnedByGameShift = isOwnedByGameShift;
        FormFactor = formFactor;
    }

    public string TargetId { get; }

    public OptimizationActionKind ActionKind { get; }

    public TargetProtection Protection { get; }

    public RecoveryAssurance RecoveryAssurance { get; }

    public bool IsApprovedTarget { get; }

    public bool IsOwnedByGameShift { get; }

    public DeviceFormFactor FormFactor { get; }
}


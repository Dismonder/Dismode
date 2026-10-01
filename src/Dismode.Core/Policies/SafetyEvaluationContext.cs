using Dismode.Core.Actions;
using Dismode.Core.Devices;

namespace Dismode.Core.Policies;

public sealed record SafetyEvaluationContext
{
    public SafetyEvaluationContext(
        string targetId,
        OptimizationActionKind actionKind,
        TargetProtection protection,
        RecoveryAssurance recoveryAssurance,
        bool isApprovedTarget,
        bool isOwnedByDismode,
        DeviceFormFactor formFactor = DeviceFormFactor.Unknown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        TargetId = targetId.Trim();
        ActionKind = actionKind;
        Protection = protection;
        RecoveryAssurance = recoveryAssurance;
        IsApprovedTarget = isApprovedTarget;
        IsOwnedByDismode = isOwnedByDismode;
        FormFactor = formFactor;
    }

    public string TargetId { get; }

    public OptimizationActionKind ActionKind { get; }

    public TargetProtection Protection { get; }

    public RecoveryAssurance RecoveryAssurance { get; }

    public bool IsApprovedTarget { get; }

    public bool IsOwnedByDismode { get; }

    public DeviceFormFactor FormFactor { get; }
}


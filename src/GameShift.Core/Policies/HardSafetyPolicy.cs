using GameShift.Core.Actions;
using GameShift.Core.Devices;

namespace GameShift.Core.Policies;

public static class HardSafetyPolicy
{
    public static SafetyDecision Evaluate(SafetyEvaluationContext context)
    {
        if (context.ActionKind is OptimizationActionKind.Observe or OptimizationActionKind.Ignore)
        {
            return Allowed(
                SafetyReasonCode.ReadOnlyAction,
                "The action does not modify the target.");
        }

        if (context.ActionKind is OptimizationActionKind.ActivateManagedPowerProfile
            && context.FormFactor == DeviceFormFactor.Desktop)
        {
            return Blocked(
                SafetyReasonCode.DesktopPowerPlanPreserved,
                "Power scheme optimization is disabled on desktop PCs to preserve the user's active power plan.");
        }

        if (context.Protection != TargetProtection.None)
        {
            return Blocked(
                SafetyReasonCode.ProtectedTarget,
                $"Target '{context.TargetId}' is protected by {context.Protection}.");
        }

        if (context.RecoveryAssurance != RecoveryAssurance.CompensationVerified)
        {
            return Blocked(
                SafetyReasonCode.RecoveryNotVerified,
                "A mutating action requires a verified compensation path.");
        }

        if (RequiresApproval(context.ActionKind) && !context.IsApprovedTarget)
        {
            return Blocked(
                SafetyReasonCode.TargetNotApproved,
                "The target has not been explicitly approved for this action.");
        }

        if (RequiresOwnership(context.ActionKind) && !context.IsOwnedByGameShift)
        {
            return Blocked(
                SafetyReasonCode.TargetNotOwnedByGameShift,
                "This action is limited to targets created or owned by GameShift.");
        }

        return Allowed(
            SafetyReasonCode.VerifiedRecovery,
            "The target is unprotected and the compensation path is verified.");
    }

    private static bool RequiresApproval(OptimizationActionKind actionKind) =>
        actionKind is
            OptimizationActionKind.GracefulCloseApplication
            or OptimizationActionKind.StopApprovedService
            or OptimizationActionKind.DeferApprovedScheduledTask
            or OptimizationActionKind.SuppressApprovedRelaunch;

    private static bool RequiresOwnership(OptimizationActionKind actionKind) =>
        actionKind is
            OptimizationActionKind.ActivateManagedPowerProfile
            or OptimizationActionKind.AssignOwnedProcessJob;

    private static SafetyDecision Allowed(
        SafetyReasonCode reasonCode,
        string explanation) =>
        new(true, reasonCode, explanation);

    private static SafetyDecision Blocked(
        SafetyReasonCode reasonCode,
        string explanation) =>
        new(false, reasonCode, explanation);
}


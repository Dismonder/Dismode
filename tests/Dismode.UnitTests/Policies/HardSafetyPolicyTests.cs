using Dismode.Core.Actions;
using Dismode.Core.Devices;
using Dismode.Core.Policies;

namespace Dismode.UnitTests.Policies;

[TestClass]
public sealed class HardSafetyPolicyTests
{
    [TestMethod]
    public void ProtectedTargetCannotBeMutatedEvenWhenApproved()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "AntiCheat.Service",
                OptimizationActionKind.StopApprovedService,
                TargetProtection.AntiCheat,
                RecoveryAssurance.CompensationVerified,
                isApprovedTarget: true,
                isOwnedByDismode: false));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.ProtectedTarget, decision.ReasonCode);
    }

    [TestMethod]
    public void ProtectedTargetCanBeObserved()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "Windows.Security",
                OptimizationActionKind.Observe,
                TargetProtection.WindowsSecurity,
                RecoveryAssurance.None,
                isApprovedTarget: false,
                isOwnedByDismode: false));

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.ReadOnlyAction, decision.ReasonCode);
    }

    [TestMethod]
    public void MutationWithoutVerifiedCompensationIsBlocked()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "Vendor.Updater",
                OptimizationActionKind.ApplyProcessEcoQos,
                TargetProtection.None,
                RecoveryAssurance.CompensationDefined,
                isApprovedTarget: true,
                isOwnedByDismode: false));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.RecoveryNotVerified, decision.ReasonCode);
    }

    [TestMethod]
    public void ApprovedLowRiskServiceWithVerifiedRecoveryIsAllowed()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "Vendor.UpdateService",
                OptimizationActionKind.StopApprovedService,
                TargetProtection.None,
                RecoveryAssurance.CompensationVerified,
                isApprovedTarget: true,
                isOwnedByDismode: false));

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.VerifiedRecovery, decision.ReasonCode);
    }

    [TestMethod]
    public void PowerProfileMustBeOwnedByDismode()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "User.ActivePowerPlan",
                OptimizationActionKind.ActivateManagedPowerProfile,
                TargetProtection.None,
                RecoveryAssurance.CompensationVerified,
                isApprovedTarget: true,
                isOwnedByDismode: false));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.TargetNotOwnedByDismode, decision.ReasonCode);
    }

    [TestMethod]
    public void PowerProfileMutationIsBlockedOnDesktopPcToPreserveActivePlan()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "Dismode.ManagedPowerPlan",
                OptimizationActionKind.ActivateManagedPowerProfile,
                TargetProtection.None,
                RecoveryAssurance.CompensationVerified,
                isApprovedTarget: true,
                isOwnedByDismode: true,
                formFactor: DeviceFormFactor.Desktop));

        Assert.IsFalse(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.DesktopPowerPlanPreserved, decision.ReasonCode);
        StringAssert.Contains(decision.Explanation, "desktop PCs");
    }

    [TestMethod]
    public void PowerProfileMutationAllowedOnLaptopWhenOwnedAndVerified()
    {
        SafetyDecision decision = HardSafetyPolicy.Evaluate(
            new SafetyEvaluationContext(
                "Dismode.ManagedPowerPlan",
                OptimizationActionKind.ActivateManagedPowerProfile,
                TargetProtection.None,
                RecoveryAssurance.CompensationVerified,
                isApprovedTarget: true,
                isOwnedByDismode: true,
                formFactor: DeviceFormFactor.Laptop));

        Assert.IsTrue(decision.IsAllowed);
        Assert.AreEqual(SafetyReasonCode.VerifiedRecovery, decision.ReasonCode);
    }
}


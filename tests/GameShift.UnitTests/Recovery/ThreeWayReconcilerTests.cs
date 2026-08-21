using GameShift.Core.Recovery;

namespace GameShift.UnitTests.Recovery;

[TestClass]
public sealed class ThreeWayReconcilerTests
{
    [TestMethod]
    public void CurrentAppliedStateRestoresOriginal()
    {
        RestoreDecision<ServiceState> decision = ThreeWayReconciler.Decide(
            ServiceState.Running,
            ServiceState.Stopped,
            ServiceState.Stopped);

        Assert.AreEqual(RestoreDecisionKind.RestoreOriginal, decision.Kind);
        Assert.IsTrue(decision.ShouldCompensate);
        Assert.IsFalse(decision.HasConflict);
    }

    [TestMethod]
    public void CurrentOriginalStateRequiresNoAction()
    {
        RestoreDecision<ServiceState> decision = ThreeWayReconciler.Decide(
            ServiceState.Running,
            ServiceState.Stopped,
            ServiceState.Running);

        Assert.AreEqual(RestoreDecisionKind.NoActionAlreadyOriginal, decision.Kind);
        Assert.IsFalse(decision.ShouldCompensate);
    }

    [TestMethod]
    public void ExternalChangeIsPreservedAndReported()
    {
        RestoreDecision<PriorityState> decision = ThreeWayReconciler.Decide(
            PriorityState.Normal,
            PriorityState.BelowNormal,
            PriorityState.High);

        Assert.AreEqual(RestoreDecisionKind.PreserveCurrentAndReportConflict, decision.Kind);
        Assert.IsFalse(decision.ShouldCompensate);
        Assert.IsTrue(decision.HasConflict);
        Assert.AreEqual(PriorityState.High, decision.CurrentState);
    }

    private enum ServiceState
    {
        Stopped,
        Running,
    }

    private enum PriorityState
    {
        BelowNormal,
        Normal,
        High,
    }
}

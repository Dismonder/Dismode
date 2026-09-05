using GameShift.Core.Activation;

namespace GameShift.UnitTests.SystemOptimization;

[TestClass]
public sealed class CompanionStartupPolicyTests
{
    [TestMethod]
    public void PerUserStartupIncludesSessionHostButExcludesSystemService()
    {
        Assert.IsTrue(
            CompanionStartupPolicy.ShouldStartForInteractiveUser(
                "GameShift.SessionHost.exe"));
        Assert.IsFalse(
            CompanionStartupPolicy.ShouldStartForInteractiveUser(
                "GameShift.SystemAgent.exe"));
    }
}

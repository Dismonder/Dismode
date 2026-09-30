using Dismode.Core.Activation;

namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class CompanionStartupPolicyTests
{
    [TestMethod]
    public void PerUserStartupIncludesSessionHostButExcludesSystemService()
    {
        Assert.IsTrue(
            CompanionStartupPolicy.ShouldStartForInteractiveUser(
                "Dismode.SessionHost.exe"));
        Assert.IsFalse(
            CompanionStartupPolicy.ShouldStartForInteractiveUser(
                "Dismode.SystemAgent.exe"));
    }
}

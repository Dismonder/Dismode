using Dismode.Windows.Platform;

namespace Dismode.IntegrationTests.Platform;

[TestClass]
public sealed class WindowsGamingEnvironmentDiagnosticTests
{
    [TestMethod]
    public void EvaluateProducesNonEmptyDiagnosticReport()
    {
        WindowsGamingDiagnosticReport report =
            WindowsGamingEnvironmentDiagnostic.Evaluate();

        Assert.IsNotNull(report);
        Assert.IsNotNull(report.Recommendations);
        Assert.IsTrue(report.Recommendations.Count > 0);
    }
}

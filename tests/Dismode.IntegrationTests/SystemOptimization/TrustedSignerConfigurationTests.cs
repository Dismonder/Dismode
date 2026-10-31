using System.Text;
using Dismode.SystemAgent.Security;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class TrustedSignerConfigurationTests
{
    private const string Thumbprint = "AABBCCDDEEFF00112233445566778899AABBCCDD";

    [TestMethod]
    public void ParseThumbprintsReadsTheJsonEmittedByBuildLocalRelease()
    {
        // Exactly what ConvertTo-Json of @{schemaVersion=1; thumbprints=@(...)}
        // writes in tools/Build-LocalRelease.ps1.
        string json =
            "{\r\n  \"schemaVersion\": 1,\r\n  \"thumbprints\": [\r\n"
            + $"    \"{Thumbprint}\"\r\n  ]\r\n}}";

        IReadOnlyList<string> result =
            TrustedSignerConfiguration.ParseThumbprints(
                Encoding.UTF8.GetBytes(json));

        CollectionAssert.AreEqual(new[] { Thumbprint }, result.ToArray());
    }

    [TestMethod]
    public void ParseThumbprintsToleratesMissingNullAndInvalidEntries()
    {
        Assert.IsEmpty(Parse("{\"schemaVersion\":1}"));
        Assert.IsEmpty(Parse("{\"schemaVersion\":1,\"thumbprints\":[null]}"));
        Assert.IsEmpty(Parse("{\"schemaVersion\":1,\"thumbprints\":[\"abc\"]}"));
        Assert.IsEmpty(Parse("{\"schemaVersion\":2,\"thumbprints\":[\""
            + Thumbprint + "\"]}"));
        Assert.IsEmpty(Parse("not json"));
    }

    private static IReadOnlyList<string> Parse(string json) =>
        TrustedSignerConfiguration.ParseThumbprints(
            Encoding.UTF8.GetBytes(json));
}

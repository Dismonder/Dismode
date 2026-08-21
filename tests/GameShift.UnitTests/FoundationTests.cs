using System.Text.Json;
using GameShift.Contracts.Diagnostics;
using GameShift.Contracts.Protocol;

namespace GameShift.UnitTests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public void DiagnosticContractSurvivesJsonRoundTrip()
    {
        ComponentStatus original = new(
            Component: "UnitTest",
            State: "Ready",
            ProtocolVersion: ProtocolInfo.CurrentVersion,
            ObservedAtUtc: DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(original);
        ComponentStatus? restored = JsonSerializer.Deserialize<ComponentStatus>(json);

        Assert.IsNotNull(restored);
        Assert.AreEqual(original, restored);
        Assert.IsLessThan(ProtocolInfo.MaximumMessageBytes, json.Length);
    }
}

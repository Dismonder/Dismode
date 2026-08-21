using System.Text;
using GameShift.Core.Activation;

namespace GameShift.UnitTests;

[TestClass]
public sealed class ActivationProtocolTests
{
    [TestMethod]
    public void ContextMenuArgumentsRequireOneExeAndPreserveBackgroundMode()
    {
        string executablePath = Path.GetFullPath("game.exe");

        GameShiftLaunchOptions options = GameShiftLaunchOptions.Parse(
        [
            "--launch-through-gameshift",
            executablePath,
            "--background",
        ]);

        Assert.IsTrue(options.StartInBackground);
        Assert.AreEqual(executablePath, options.GameExecutablePath);
    }

    [TestMethod]
    public void UnknownOrNonExeArgumentsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            GameShiftLaunchOptions.Parse(["--run", "anything"]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            GameShiftLaunchOptions.Parse(
            [
                "--launch-through-gameshift",
                Path.GetFullPath("script.ps1"),
            ]));
    }

    [TestMethod]
    public void TypedActivationMessageRoundTrips()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UiActivationRequest expected = new(
            UiActivationProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            now,
            Path.GetFullPath("game.exe"),
            KeepWindowHidden: true);

        byte[] payload = UiActivationProtocol.Serialize(expected);
        UiActivationRequest actual =
            UiActivationProtocol.DeserializeAndValidate(payload, now);

        Assert.AreEqual(expected.RequestId, actual.RequestId);
        Assert.AreEqual(expected.GameExecutablePath, actual.GameExecutablePath);
        Assert.IsTrue(actual.KeepWindowHidden);
    }

    [TestMethod]
    public void StaleOrExtendedActivationMessageIsRejected()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string path = Path.GetFullPath("game.exe").Replace("\\", "\\\\");
        string stale = $$"""
            {
              "schemaVersion": 1,
              "requestId": "{{Guid.NewGuid():D}}",
              "requestedAtUtc": "{{now.AddMinutes(-5):O}}",
              "gameExecutablePath": "{{path}}",
              "keepWindowHidden": true
            }
            """;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiActivationProtocol.DeserializeAndValidate(
                Encoding.UTF8.GetBytes(stale),
                now));

        string extended = stale.Replace(
            "\"keepWindowHidden\": true",
            "\"keepWindowHidden\": true, \"command\": \"anything\"",
            StringComparison.Ordinal);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            UiActivationProtocol.DeserializeAndValidate(
                Encoding.UTF8.GetBytes(extended),
                now.AddMinutes(-5)));
    }
}

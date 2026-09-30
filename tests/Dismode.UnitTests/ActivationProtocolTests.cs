using System.Text;
using Dismode.Core.Activation;

namespace Dismode.UnitTests;

[TestClass]
public sealed class ActivationProtocolTests
{
    [TestMethod]
    public void ContextMenuArgumentsRequireOneExeAndPreserveBackgroundMode()
    {
        string executablePath = Path.GetFullPath("game.exe");

        DismodeLaunchOptions options = DismodeLaunchOptions.Parse(
        [
            "--launch-through-dismode",
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
            DismodeLaunchOptions.Parse(["--run", "anything"]));
        Assert.ThrowsExactly<ArgumentException>(() =>
            DismodeLaunchOptions.Parse(
            [
                "--launch-through-dismode",
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

    [TestMethod]
    public void ShowOnlyRoundTripsAndDefaultsToFalseForOlderSenders()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UiActivationRequest showOnly = new(
            UiActivationProtocol.CurrentSchemaVersion,
            Guid.NewGuid(),
            now,
            Path.GetFullPath("Dismode.UI.exe"),
            KeepWindowHidden: false,
            ShowOnly: true);

        byte[] showOnlyPayload = UiActivationProtocol.Serialize(showOnly);
        UiActivationRequest actual = UiActivationProtocol.DeserializeAndValidate(
            showOnlyPayload,
            now);
        Assert.IsTrue(actual.ShowOnly);
        Assert.Contains(
            "showOnly",
            Encoding.UTF8.GetString(showOnlyPayload));

        // Zwykly start gry nie niesie nowego pola: starsze okno odrzuca
        // nieznane skladowe, a start gry ma dzialac miedzy wersjami.
        string plainPayload = Encoding.UTF8.GetString(
            UiActivationProtocol.Serialize(showOnly with { ShowOnly = false }));
        Assert.DoesNotContain("showOnly", plainPayload);

        // Launcher sprzed tej wersji nie wysyla tego pola: znaczenie zostaje
        // takie, jakie mial zawsze — uruchom gre.
        string path = Path.GetFullPath("game.exe").Replace("\\", "\\\\");
        string legacy = $$"""
            {
              "schemaVersion": 1,
              "requestId": "{{Guid.NewGuid():D}}",
              "requestedAtUtc": "{{now:O}}",
              "gameExecutablePath": "{{path}}",
              "keepWindowHidden": false
            }
            """;
        UiActivationRequest fromLegacy =
            UiActivationProtocol.DeserializeAndValidate(
                Encoding.UTF8.GetBytes(legacy),
                now);
        Assert.IsFalse(fromLegacy.ShowOnly);
    }
}

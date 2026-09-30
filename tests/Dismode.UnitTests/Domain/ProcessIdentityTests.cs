using System.Text.Json;
using Dismode.Core.Domain.Processes;

namespace Dismode.UnitTests.Domain;

[TestClass]
public sealed class ProcessIdentityTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 28, 18, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void RuntimeMatchUsesPidStartPathUserAndSession()
    {
        ProcessIdentity original = CreateIdentity(
            processId: 4242,
            startedAtUtc: StartedAtUtc,
            executablePath: @"C:\Games\Game.exe");
        ProcessIdentity candidate = CreateIdentity(
            processId: 4242,
            startedAtUtc: StartedAtUtc,
            executablePath: @"c:\games\GAME.exe");

        Assert.IsTrue(original.MatchesRuntimeIdentity(candidate));
        Assert.IsTrue(original.MatchesExecutable(candidate));
    }

    [TestMethod]
    public void ReusedPidWithDifferentStartTimeDoesNotMatch()
    {
        ProcessIdentity original = CreateIdentity(4242, StartedAtUtc, @"C:\Games\Game.exe");
        ProcessIdentity reused = CreateIdentity(
            4242,
            StartedAtUtc.AddSeconds(1),
            @"C:\Games\Game.exe");

        Assert.IsFalse(original.MatchesRuntimeIdentity(reused));
    }

    [TestMethod]
    public void ConstructorRejectsRelativeExecutablePath()
    {
        ProcessRuntimeKey runtimeKey = new(4242, StartedAtUtc);

        Assert.ThrowsExactly<ArgumentException>(
            () => new ProcessIdentity(
                runtimeKey,
                "Game.exe",
                new string('A', 64),
                "Verified Publisher",
                "S-1-5-21-1000",
                1,
                null));
    }

    [TestMethod]
    public void RuntimeKeyRoundTripsThroughJsonWithoutLosingIdentity()
    {
        ProcessRuntimeKey original = new(4242, StartedAtUtc);

        string json = JsonSerializer.Serialize(original);
        ProcessRuntimeKey restored =
            JsonSerializer.Deserialize<ProcessRuntimeKey>(json);

        Assert.AreEqual(original, restored);
    }

    private static ProcessIdentity CreateIdentity(
        int processId,
        DateTimeOffset startedAtUtc,
        string executablePath) =>
        new(
            new ProcessRuntimeKey(processId, startedAtUtc),
            executablePath,
            new string('A', 64),
            "Verified Publisher",
            "S-1-5-21-1000",
            1,
            null);
}

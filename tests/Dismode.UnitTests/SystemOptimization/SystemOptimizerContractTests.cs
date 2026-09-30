using Dismode.Contracts.Commands;
using Dismode.Contracts.Protocol;

namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerContractTests
{
    [TestMethod]
    public void VersionAndExistingCommandNumbersRemainStable()
    {
        object? protocolVersion = typeof(ProtocolInfo)
            .GetField(nameof(ProtocolInfo.CurrentVersion))
            ?.GetRawConstantValue();
        Dictionary<string, int> commandNumbers = Enum
            .GetValues<CommandKind>()
            .ToDictionary(value => value.ToString(), value => (int)value);

        Assert.AreEqual(6, protocolVersion);
        Assert.AreEqual(1, commandNumbers[nameof(CommandKind.GetComponentStatus)]);
        Assert.AreEqual(9, commandNumbers[nameof(CommandKind.ActivateManagedPowerProfile)]);
        Assert.AreEqual(13, commandNumbers[nameof(CommandKind.SetFrameRateTracking)]);
        Assert.AreEqual(14, commandNumbers[nameof(CommandKind.GetSystemOptimizerStatus)]);
        Assert.AreEqual(24, commandNumbers[nameof(CommandKind.SubmitBenchmarkCapture)]);
        Assert.AreEqual(27, commandNumbers[nameof(CommandKind.ActivatePerGameOptimizationProfile)]);
        Assert.AreEqual(28, commandNumbers[nameof(CommandKind.RestorePerGameOptimizationProfile)]);
    }

    [TestMethod]
    public void ProfilesReplyCanReturnTheOwnersCompleteGameProfileList()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Dismode.Contracts",
            "Grpc",
            "dismode.proto"));

        StringAssert.Contains(
            source,
            "repeated string per_game_profiles_json = 3;");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Dismode repository root not found.");
    }
}

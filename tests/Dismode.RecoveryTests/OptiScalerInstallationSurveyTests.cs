using Dismode.Data.Storage;

namespace Dismode.RecoveryTests;

/// <summary>
/// The uninstall gate depends on this answer, so its failure modes matter as
/// much as its happy path. Saying "nothing installed" when something is would
/// let the uninstaller strand mod files inside somebody's game with no tool
/// left that can remove them.
/// </summary>
[TestClass]
public sealed class OptiScalerInstallationSurveyTests
{
    [TestMethod]
    public void NothingIsReportedWhenNoInstallationsExist()
    {
        using TemporaryDirectory directory = new();

        Assert.IsNull(OptiScalerInstallationSurvey.Describe(directory.Path));
    }

    [TestMethod]
    public void AMissingDirectoryIsNotAnInstallation()
    {
        using TemporaryDirectory directory = new();
        string missing = Path.Combine(directory.Path, "never-created");

        Assert.IsNull(OptiScalerInstallationSurvey.Describe(missing));
    }

    [TestMethod]
    public void InstalledGamesAreNamed()
    {
        using TemporaryDirectory directory = new();
        WriteManifest(directory.Path, "one", @"C:\Games\Valheim\valheim.exe");
        WriteManifest(directory.Path, "two", @"D:\Games\RE9\re9.exe");

        string? described =
            OptiScalerInstallationSurvey.Describe(directory.Path);

        Assert.IsNotNull(described);
        StringAssert.Contains(described, "2");
        StringAssert.Contains(described, "valheim.exe");
        StringAssert.Contains(described, "re9.exe");
    }

    /// <summary>
    /// A manifest nobody can parse still describes files that are on disk.
    /// Treating it as "nothing here" would be the one answer that loses them.
    /// </summary>
    [TestMethod]
    public void AnUnreadableManifestStillCountsAsAnInstallation()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(
            Path.Combine(directory.Path, "broken.json"),
            "{ this is not json");

        string? described =
            OptiScalerInstallationSurvey.Describe(directory.Path);

        Assert.IsNotNull(
            described,
            "Uszkodzony manifest musi liczyć się jako instalacja — opisane "
                + "przez niego pliki nadal leżą w katalogu gry.");
        StringAssert.Contains(described, "nieznana gra");
    }

    private static void WriteManifest(
        string directory,
        string name,
        string targetExecutablePath) =>
        File.WriteAllText(
            Path.Combine(directory, name + ".json"),
            $$"""
            { "targetExecutablePath": {{System.Text.Json.JsonSerializer
                .Serialize(targetExecutablePath)}} }
            """);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Dismode-survey-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

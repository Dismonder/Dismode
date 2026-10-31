using Dismode.Windows.Profiles;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class InstalledGameDiscoveryServiceTests
{
    [TestMethod]
    public void GameFolderNameWithPenalizedTermDoesNotHideExecutable()
    {
        using TempDirectory root = new();
        string installDirectory = Path.Combine(
            root.Path,
            "Support",
            "Steam",
            "steamapps",
            "common",
            "Crash Bandicoot - N Sane Trilogy");
        string executable = CreateFile(
            installDirectory,
            "Bandicoot.exe");

        string? found = InstalledGameDiscoveryService
            .FindLikelyGameExecutable(
                installDirectory,
                "Crash Bandicoot N. Sane Trilogy",
                "Crash Bandicoot - N Sane Trilogy",
                CancellationToken.None);

        Assert.AreEqual(executable, found);
    }

    [TestMethod]
    public void PenalizedFilesAndFoldersInsideInstallDirectoryStayExcluded()
    {
        using TempDirectory root = new();
        string installDirectory = Path.Combine(root.Path, "Game");
        CreateFile(installDirectory, "CrashHandler.exe");
        CreateFile(Path.Combine(installDirectory, "Launcher"), "Real.exe");

        string? found = InstalledGameDiscoveryService
            .FindLikelyGameExecutable(
                installDirectory,
                "Game",
                "Game",
                CancellationToken.None);

        Assert.IsNull(found);
    }

    [TestMethod]
    public void LockedManifestIsSkippedInsteadOfThrowing()
    {
        using TempDirectory root = new();
        string manifest = Path.Combine(root.Path, "appmanifest_1.acf");
        File.WriteAllText(manifest, "\"appid\" \"1\"");
        using FileStream locked = new(
            manifest,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Dictionary<string, string> values =
            InstalledGameDiscoveryService.ReadFlatVdf(manifest);

        Assert.AreEqual(0, values.Count);
    }

    private static string CreateFile(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DismodeDiscovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

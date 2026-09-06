using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GameShift.UnitTests;

[TestClass]
public sealed class ReleaseVersionAndUpdateUiContractTests
{
    [TestMethod]
    public void ReleasePropertiesDeriveFromSingleVersionLiteral()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument properties = XDocument.Load(
            Path.Combine(repositoryRoot, "Directory.Build.props"));
        XElement group = properties.Root!
            .Elements("PropertyGroup")
            .Single();
        XElement version = group.Element("Version")!;
        XElement assemblyVersion = group.Element("AssemblyVersion")!;
        XElement fileVersion = group.Element("FileVersion")!;
        XElement informationalVersion = group.Element(
            "InformationalVersion")!;

        Assert.AreEqual("0.4.8", version.Value);
        Assert.AreEqual("$(Version).0", assemblyVersion.Value);
        Assert.AreEqual("$(Version).0", fileVersion.Value);
        Assert.AreEqual(
            "$(Version)-gaming-edition",
            informationalVersion.Value);
        Assert.IsTrue(
            version.NodesBeforeSelf().Count()
                < assemblyVersion.NodesBeforeSelf().Count()
            && version.NodesBeforeSelf().Count()
                < fileVersion.NodesBeforeSelf().Count()
            && version.NodesBeforeSelf().Count()
                < informationalVersion.NodesBeforeSelf().Count());
        Assert.AreEqual(
            1,
            Regex.Count(
                properties.ToString(),
                @">\d+\.\d+\.\d+(?:\.\d+)?(?:-[^<]+)?<"));
    }

    [TestMethod]
    public void WindowsPowerShellReleaseScriptsCarryUtf8Bom()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] scriptPaths =
        [
            Path.Combine(repositoryRoot, "tools", "Build-Installer.ps1"),
            Path.Combine(repositoryRoot, "tools", "Build-LocalRelease.ps1"),
        ];

        foreach (string scriptPath in scriptPaths)
        {
            byte[] bytes = File.ReadAllBytes(scriptPath);

            Assert.IsGreaterThanOrEqualTo(
                3,
                bytes.Length,
                $"Skrypt {scriptPath} jest pusty.");
            Assert.AreEqual(0xEF, bytes[0], scriptPath);
            Assert.AreEqual(0xBB, bytes[1], scriptPath);
            Assert.AreEqual(0xBF, bytes[2], scriptPath);
        }
    }

    [TestMethod]
    public void NullManifestClearsPreviousDownloadBeforeStateDispatch()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GameShift.UI",
            "MainWindow.xaml.cs"));
        int resultIndex = source.IndexOf(
            "UpdateCheckResult result = await _updates.CheckAsync(",
            StringComparison.Ordinal);
        int clearIndex = source.IndexOf(
            "if (result.Manifest is null)",
            resultIndex,
            StringComparison.Ordinal);
        int switchIndex = source.IndexOf(
            "switch (result.State)",
            resultIndex,
            StringComparison.Ordinal);
        Assert.IsGreaterThan(resultIndex, clearIndex);
        Assert.IsLessThan(switchIndex, clearIndex);
        string clearBlock = source[clearIndex..switchIndex];
        StringAssert.Contains(clearBlock, "_availableUpdate = null;");
        StringAssert.Contains(clearBlock, "_stagedInstallerPath = null;");
    }

    [TestMethod]
    public void UserCancellationCatchUsesBareRethrow()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GameShift.UI",
            "Services",
            "UpdateClientService.cs"));

        StringAssert.Matches(
            source,
            new Regex(
                @"catch \(OperationCanceledException exception\)\s+when \(\s*UpdateFailurePolicy\.IsUserCancellation\(\s*exception,\s*cancellationToken\)\)\s+\{\s+throw;\s+\}",
                RegexOptions.CultureInvariant));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Nie znaleziono katalogu repozytorium GameShift.");
    }
}

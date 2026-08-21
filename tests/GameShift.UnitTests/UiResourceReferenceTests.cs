using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GameShift.UnitTests;

[TestClass]
public sealed class UiResourceReferenceTests
{
    private static readonly string[] ExpectedMissingNestedResourceKeys =
        ["GameShiftMissingBrush"];

    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly Regex DefinedResourcePattern = new(
        "x:Key\\s*=\\s*[\"'](?<key>GameShift[A-Za-z0-9_.-]+)[\"']",
        RegexOptions.CultureInvariant);

    private static readonly Regex ReferencedResourcePattern = new(
        "\\{(?:ThemeResource|StaticResource)\\s+"
            + "(?:ResourceKey\\s*=\\s*)?"
            + "(?<key>GameShift[A-Za-z0-9_.-]+)"
            + "(?:\\s*,[^}]*)?\\s*\\}",
        RegexOptions.CultureInvariant);

    [TestMethod]
    public void EveryCustomUiResourceReferenceHasADefinition()
    {
        string repositoryRoot = FindRepositoryRoot();
        string uiDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "GameShift.UI");
        string[] missing = FindMissingResourceKeys(uiDirectory);

        Assert.IsEmpty(
            missing,
            "Brak definicji zasobów UI: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void NestedResourceDictionariesAndViewsAreValidated()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"GameShift-UiResources-{Guid.NewGuid():N}");

        try
        {
            string themesDirectory = Path.Combine(
                temporaryDirectory,
                "Themes");
            string viewsDirectory = Path.Combine(
                temporaryDirectory,
                "Views");
            Directory.CreateDirectory(themesDirectory);
            Directory.CreateDirectory(viewsDirectory);
            File.WriteAllText(
                Path.Combine(themesDirectory, "Colors.xaml"),
                "<ResourceDictionary x:Key=\"GameShiftNestedBrush\" />");
            File.WriteAllText(
                Path.Combine(viewsDirectory, "Dashboard.xaml"),
                "<Grid Background=\"{ThemeResource GameShiftNestedBrush}\" "
                    + "BorderBrush=\"{StaticResource GameShiftMissingBrush}\" />");

            string[] missing = FindMissingResourceKeys(temporaryDirectory);

            CollectionAssert.AreEqual(
                ExpectedMissingNestedResourceKeys,
                missing);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UiPublishTargetDiscoversCompiledXamlRecursively()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "GameShift.UI",
            "GameShift.UI.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement target = project
            .Descendants("Target")
            .Single(element => string.Equals(
                (string?)element.Attribute("Name"),
                "CopyLooseWinUiResourcesToPublish",
                StringComparison.Ordinal));
        XElement xbfItem = target.Descendants("_LooseWinUiXbf").Single();
        XElement priItem = target
            .Descendants("_LooseWinUiResource")
            .Single(element => ((string?)element.Attribute("Include"))
                ?.EndsWith(".pri", StringComparison.Ordinal) == true);
        XElement copy = target.Descendants("Copy").Single();

        Assert.AreEqual(
            "$(TargetDir)**\\*.xbf",
            (string?)xbfItem.Attribute("Include"));
        StringAssert.Contains(
            (string?)xbfItem.Attribute("Exclude") ?? string.Empty,
            "$(PublishDir)**\\*.xbf");
        Assert.AreEqual(
            "$(TargetDir)$(TargetName).pri",
            (string?)priItem.Attribute("Include"));
        StringAssert.Contains(
            (string?)copy.Attribute("DestinationFiles") ?? string.Empty,
            "%(RecursiveDir)");
    }

    private static string[] FindMissingResourceKeys(string uiDirectory)
    {
        string[] xamlFiles = EnumerateSourceXamlFiles(uiDirectory).ToArray();
        HashSet<string> definitions = xamlFiles
            .SelectMany(path => DefinedResourcePattern
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        return xamlFiles
            .SelectMany(path => ReferencedResourcePattern
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(key => !definitions.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateSourceXamlFiles(
        string uiDirectory)
    {
        return Directory
            .EnumerateFiles(uiDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(uiDirectory, path));
    }

    private static bool IsBuildOutputPath(
        string uiDirectory,
        string path)
    {
        string relativePath = Path.GetRelativePath(uiDirectory, path);
        string[] segments = relativePath.Split(
            DirectorySeparators,
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Nie znaleziono katalogu repozytorium GameShift.");
    }
}

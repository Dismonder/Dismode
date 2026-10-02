namespace Dismode.UnitTests;

[TestClass]
public sealed class LocalArtworkBindingTests
{
    private static readonly string[] ExpectedHistoryRows = ["0", "1", "2", "3"];

    [TestMethod]
    public void LocalArtworkUsesSeparatePosterAndHeroStreamsWithFallback()
    {
        string repositoryRoot = FindRepositoryRoot();
        string uiDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "Dismode.UI");
        string xaml = File.ReadAllText(
            Path.Combine(uiDirectory, "MainWindow.xaml"));
        string mainWindow = File.ReadAllText(
            Path.Combine(uiDirectory, "MainWindow.xaml.cs"));
        string loader = File.ReadAllText(
            Path.Combine(
                uiDirectory,
                "Services",
                "LocalArtworkImageLoader.cs"));
        string profileItem = File.ReadAllText(
            Path.Combine(
                uiDirectory,
                "ViewModels",
                "ProfileListItem.cs"));

        StringAssert.Contains(xaml, "{Binding TileArtworkSource}");
        StringAssert.Contains(xaml, "{Binding LibraryArtworkSource}");
        StringAssert.Contains(xaml, "{Binding TileArtworkStretch}");
        StringAssert.Contains(xaml, "{Binding LibraryArtworkStretch}");
        Assert.IsFalse(xaml.Contains(
            "Source=\"{Binding ArtworkSource}\"",
            StringComparison.Ordinal));
        Assert.IsFalse(
            xaml.Contains("ArtworkUri", StringComparison.Ordinal));
        Assert.IsFalse(
            profileItem.Contains("ArtworkUri", StringComparison.Ordinal));
        StringAssert.Contains(loader, ".GetFileFromPathAsync(fullPath)");
        StringAssert.Contains(loader, ".OpenReadAsync()");
        StringAssert.Contains(loader, ".SetSourceAsync(stream)");
        StringAssert.Contains(mainWindow, "item.PosterArtworkSource");
        StringAssert.Contains(mainWindow, "item.HeroArtworkSource");
        StringAssert.Contains(mainWindow, "profile?.HeroArtworkSource");
        StringAssert.Contains(mainWindow, "profile?.PosterArtworkSource");
        StringAssert.Contains(mainWindow, "?? _dashboardFallbackArtwork");
    }

    [TestMethod]
    public void HistoryHeaderSummaryNotificationAndListHaveSeparateRows()
    {
        System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Load(
            Path.Combine(FindRepositoryRoot(), "src", "Dismode.UI", "MainWindow.xaml"));
        System.Xml.Linq.XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        System.Xml.Linq.XElement history = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "HistoryPage");
        System.Xml.Linq.XElement[] content = history.Elements()
            .Where(element => element.Name.LocalName is "Border" or "Grid" or "InfoBar").ToArray();
        CollectionAssert.AreEqual(ExpectedHistoryRows,
            content.Select(element => (string?)element.Attribute("Grid.Row")).ToArray());
        Assert.AreEqual(4, history.Elements().Single(element => element.Name.LocalName == "Grid.RowDefinitions").Elements().Count());
        Assert.AreEqual("Collapsed", (string?)content[1].Attribute("Visibility"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Nie znaleziono katalogu repozytorium Dismode.");
    }
}

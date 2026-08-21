namespace GameShift.UnitTests;

[TestClass]
public sealed class LocalArtworkBindingTests
{
    [TestMethod]
    public void LocalArtworkUsesStreamLoadingAndKeepsFallback()
    {
        string repositoryRoot = FindRepositoryRoot();
        string uiDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "GameShift.UI");
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

        StringAssert.Contains(
            xaml,
            "Source=\"{Binding ArtworkSource}\"");
        Assert.IsFalse(
            xaml.Contains("ArtworkUri", StringComparison.Ordinal));
        Assert.IsFalse(
            profileItem.Contains("ArtworkUri", StringComparison.Ordinal));
        StringAssert.Contains(loader, ".GetFileFromPathAsync(fullPath)");
        StringAssert.Contains(loader, ".OpenReadAsync()");
        StringAssert.Contains(loader, ".SetSourceAsync(stream)");
        StringAssert.Contains(mainWindow, "profile?.ArtworkSource");
        StringAssert.Contains(mainWindow, "?? _dashboardFallbackArtwork");
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

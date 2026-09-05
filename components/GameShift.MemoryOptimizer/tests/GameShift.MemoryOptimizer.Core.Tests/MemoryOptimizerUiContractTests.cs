using System.Xml.Linq;

namespace GameShift.MemoryOptimizer.Core.Tests;

[TestClass]
public sealed class MemoryOptimizerUiContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void ShellRequestsDarkThemeBeforeTheFirstFrame()
    {
        XElement application = XDocument.Load(Path.Combine(
            FindComponentRoot(), "src", "GameShift.MemoryOptimizer", "App.xaml")).Root!;
        Assert.AreEqual("Dark", (string?)application.Attribute("RequestedTheme"),
            "The application must select dark resources before creating the window.");
        XElement root = Named("RootLayout");
        Assert.AreEqual("Dark", (string?)root.Attribute("RequestedTheme"),
            "A dark caption paired with the system light XAML theme produces unreadable controls.");
    }

    [TestMethod]
    public void RamGaugeIsDeterminateAndActive()
    {
        XElement ring = Named("RamUsageRing");
        Assert.AreEqual("False", (string?)ring.Attribute("IsIndeterminate"),
            "A value gauge must not use the indeterminate spinner template.");
        Assert.AreEqual("True", (string?)ring.Attribute("IsActive"));
    }

    [TestMethod]
    public void CompactPanelContainsAStatusAndSeparateFeedbackSurface()
    {
        XElement compact = Named("CompactShell");
        Assert.IsTrue(compact.Descendants().Any(element =>
            (string?)element.Attribute(Xaml + "Name") == "CompactStatusText"));
        Assert.IsTrue(compact.Descendants().Any(element =>
            (string?)element.Attribute(Xaml + "Name") == "CompactFeedbackText"),
            "Errors in the hidden normal shell cannot explain failed compact actions.");
    }

    [TestMethod]
    public void CoreNavigationScreensAreDistinctAndOnlyOverviewStartsVisible()
    {
        string[] names =
        [
            "OverviewPage", "AutomationPage", "ProcessesPage",
            "HistoryPage", "AdvancedPage",
        ];
        foreach (string name in names)
        {
            XElement page = Named(name);
            Assert.AreEqual("Disabled", (string?)page.Attribute("HorizontalScrollMode"));
            Assert.AreEqual(
                name == "OverviewPage" ? null : "Collapsed",
                (string?)page.Attribute("Visibility"));
        }
    }

    [TestMethod]
    public void ProcessAndHistoryRowsStretchWithinVirtualizedLists()
    {
        foreach (string name in new[] { "ProcessList", "HistoryList", "ExclusionList" })
        {
            XElement list = Named(name);
            Assert.IsTrue(list.Elements().Any(element =>
                element.Name.LocalName == "ListView.ItemContainerStyle"),
                $"{name}: default container alignment can clip row actions.");
        }
    }

    [TestMethod]
    public void CommitMetricCanWrapInsteadOfClippingAtCompactCardWidth()
    {
        Assert.AreEqual("Wrap", (string?)Named("CommitRamText").Attribute("TextWrapping"));
    }

    [TestMethod]
    public void OperationFeedbackReservesLayoutSpaceInsteadOfReflowingTheOverview()
    {
        foreach (string name in new[]
                 {
                     "AutomationSummaryDetails",
                     "LastResultText",
                     "OptimizationBlockReasonText",
                 })
        {
            XElement text = Named(name);
            Assert.IsNotNull(text.Attribute("MinHeight"));
            Assert.IsNotNull(text.Attribute("Height"));
            Assert.AreEqual("2", (string?)text.Attribute("MaxLines"));
            Assert.AreEqual(
                "CharacterEllipsis",
                (string?)text.Attribute("TextTrimming"));
        }

        foreach (string name in new[]
                 {
                     "OptimizeProgressRing",
                     "SaveProgressRing",
                     "CompactBusyProgress",
                 })
        {
            XElement progress = Named(name);
            Assert.AreEqual("Visible", (string?)progress.Attribute("Visibility"));
            Assert.AreEqual("0", (string?)progress.Attribute("Opacity"));
        }

        XElement compactNotice = Named("CompactDraftNotice");
        Assert.AreEqual("Visible", (string?)compactNotice.Attribute("Visibility"));
        Assert.AreEqual("0", (string?)compactNotice.Attribute("Opacity"));

        XElement dirtyBar = Named("DirtyBar");
        Assert.AreEqual("Top", (string?)dirtyBar.Attribute("VerticalAlignment"));
        Assert.AreEqual("Visible", (string?)dirtyBar.Attribute("Visibility"));
        Assert.AreEqual("0", (string?)dirtyBar.Attribute("Opacity"));
        Assert.AreEqual("False", (string?)dirtyBar.Attribute("IsHitTestVisible"));

        Assert.AreEqual("168", (string?)Named("OptimizeButton").Attribute("Width"));
        Assert.AreEqual("42", (string?)Named("OptimizeButton").Attribute("Height"));
        Assert.AreEqual("154", (string?)Named("PauseButton").Attribute("Width"));
        Assert.AreEqual("42", (string?)Named("PauseButton").Attribute("Height"));
        Assert.AreEqual("160", (string?)Named("RamUsageSummaryText").Attribute("Width"));
        Assert.AreEqual("100", (string?)Named("ServiceVersionText").Attribute("Width"));
        Assert.AreEqual("116", (string?)Named("CompactOptimizeButton").Attribute("Width"));
        Assert.AreEqual("32", (string?)Named("CompactOptimizeButton").Attribute("Height"));
        Assert.AreEqual("96", (string?)Named("CompactPauseButton").Attribute("Width"));
        Assert.AreEqual("32", (string?)Named("CompactPauseButton").Attribute("Height"));
        Assert.AreEqual("32", (string?)Named("CompactExpandButton").Attribute("Height"));
    }

    [TestMethod]
    public void RefreshSurfacesKeepStableGeometry()
    {
        XElement root = Named("RootLayout");
        Assert.AreEqual("True", (string?)root.Attribute("UseLayoutRounding"));

        XElement titleBadge = Named("TitleBarStatusBadge");
        Assert.AreEqual("92", (string?)titleBadge.Attribute("Width"));
        Assert.AreEqual("24", (string?)titleBadge.Attribute("Height"));

        XElement titleStatus = Named("TitleBarStatusText");
        Assert.AreEqual("NoWrap", (string?)titleStatus.Attribute("TextWrapping"));
        Assert.AreEqual("CharacterEllipsis", (string?)titleStatus.Attribute("TextTrimming"));

        foreach (string name in new[]
                 { "OverviewPage", "AutomationPage", "ProcessesPage", "HistoryPage", "AdvancedPage" })
        {
            Assert.AreEqual(
                "Hidden",
                (string?)Named(name).Attribute("VerticalScrollBarVisibility"),
                $"{name}: scrollbar appearance must not move the page while status is refreshed.");
        }
    }

    [TestMethod]
    public void StatusRefreshDoesNotMoveOrResizeTheWindow()
    {
        string source = File.ReadAllText(Path.Combine(
            FindComponentRoot(), "src", "GameShift.MemoryOptimizer", "MainWindow.xaml.cs"));

        Assert.IsFalse(source.Contains(
            "AppWindow.Resize(",
            StringComparison.Ordinal),
            "The window must not be resized from the constructor or a status update.");
        StringAssert.Contains(source, "resizeWindow: loadSettings");
        StringAssert.Contains(source, "settingsChanged = _settingsDraft.Receive(status.Settings);");
        StringAssert.Contains(source,
            "if (!_dialogOpen && acceptSettings && (loadSettings || settingsChanged))");
        StringAssert.Contains(source,
            "if (!firstApply && !modeChanged && !alwaysOnTopChanged && !resizeWindow)");
        StringAssert.Contains(source, "if (resizeWindow && (firstApply || modeChanged))");
        StringAssert.Contains(source, "RectInt32 target = new(x, y, width, height);");
    }

    [TestMethod]
    public void ServiceStatusStripHasFixedHeightAndSingleLineFeedback()
    {
        XElement strip = Named("ServiceStatusStrip");
        Assert.AreEqual("64", (string?)strip.Attribute("Height"));

        XElement title = Named("ServiceStatusTitle");
        Assert.AreEqual("NoWrap", (string?)title.Attribute("TextWrapping"));

        XElement message = Named("ServiceStatusMessage");
        Assert.AreEqual("NoWrap", (string?)message.Attribute("TextWrapping"));
    }

    [TestMethod]
    public void AdvancedProfileCheckboxesHaveAccessibleNames()
    {
        XElement panel = Named("AdvancedUnlockedPanel");
        XElement[] options = panel.Descendants()
            .Where(element => element.Name.LocalName == "CheckBox")
            .ToArray();
        Assert.HasCount(16, options);
        foreach (XElement option in options)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                (string?)option.Attribute("AutomationProperties.Name")),
                $"Unnamed advanced checkbox: {option.Attribute(Xaml + "Name")}");
        }
    }

    [TestMethod]
    public void NarrowStateStacksFormsAndPersistentActions()
    {
        XDocument source = XDocument.Load(Path.Combine(FindComponentRoot(), "src",
            "GameShift.MemoryOptimizer", "MainWindow.xaml"));
        XElement narrow = source.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "NarrowWindow");
        HashSet<string> targets = narrow.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(static target => target is not null)
            .Select(static target => target!)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "AutomationConditionsSecondColumn.Width",
            "CpuThresholdBox.(Grid.Row)",
            "ScheduleSecondaryColumn.Width",
            "ScheduleIntervalBox.(Grid.Row)",
            "HistoryHeaderAction.(Grid.Row)",
            "DirtyActions.(Grid.Row)",
        ];
        foreach (string target in required)
        {
            Assert.Contains(target, targets,
                $"Narrow layout does not reposition {target}.");
        }
    }

    [TestMethod]
    public void NarrowStateCompactsAdvancedProfilesWithoutHorizontalClipping()
    {
        XDocument source = LoadMainWindow();
        XElement narrow = source.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "NarrowWindow");
        HashSet<string> targets = narrow.Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(static target => target is not null)
            .Select(static target => target!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("AdvancedProfileManualColumn.Width", targets);
        Assert.Contains("AdvancedProfileAutomaticColumn.Width", targets);

        XElement grid = Named("AdvancedProfileGrid");
        XElement[] labels = grid.Elements()
            .Where(element => element.Name.LocalName == "TextBlock" &&
                int.TryParse((string?)element.Attribute("Grid.Row"), out int row) && row > 0)
            .ToArray();
        Assert.HasCount(8, labels);
        Assert.IsTrue(labels.All(label =>
            (string?)label.Attribute("TextWrapping") == "Wrap"));
    }

    [TestMethod]
    public void HighContrastUsesTheUsersSystemPalette()
    {
        XDocument app = XDocument.Load(Path.Combine(
            FindComponentRoot(), "src", "GameShift.MemoryOptimizer", "App.xaml"));
        XElement highContrast = app.Descendants().Single(element =>
            element.Name.LocalName == "ResourceDictionary" &&
            (string?)element.Attribute(Xaml + "Key") == "HighContrast");
        XElement[] brushes = highContrast.Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .ToArray();
        Assert.IsNotEmpty(brushes);
        Assert.IsTrue(brushes.All(brush =>
            ((string?)brush.Attribute("Color"))?.StartsWith(
                "{ThemeResource SystemColor", StringComparison.Ordinal) == true),
            "High Contrast colors must follow the active Windows palette.");

        XElement banner = Named("AdvancedWarningBanner");
        Assert.AreEqual("{ThemeResource MemoryWarningSoftBrush}",
            (string?)banner.Attribute("Background"));
    }

    [TestMethod]
    public void TitleBarHighContrastAvoidsTheCrashingLegacyWinRtActivation()
    {
        string source = File.ReadAllText(Path.Combine(
            FindComponentRoot(), "src", "GameShift.MemoryOptimizer", "MainWindow.xaml.cs"));

        Assert.IsFalse(source.Contains(
            "AccessibilitySettings",
            StringComparison.Ordinal),
            "AccessibilitySettings can inherit WinUI's pending composition error and fail-fast at startup.");
        StringAssert.Contains(source, "SystemParametersInfo");
        StringAssert.Contains(source, "RootLayout.ActualThemeChanged");
    }

    [TestMethod]
    public void AppManifestDeclaresPerMonitorV2DpiAwareness()
    {
        XDocument manifest = XDocument.Load(Path.Combine(
            FindComponentRoot(), "src", "GameShift.MemoryOptimizer", "app.manifest"));

        XElement dpiAwareness = manifest.Descendants().Single(element =>
            element.Name.LocalName == "dpiAwareness");
        Assert.AreEqual("PerMonitorV2", dpiAwareness.Value.Trim());

        XElement longPathAware = manifest.Descendants().Single(element =>
            element.Name.LocalName == "longPathAware");
        Assert.AreEqual("true", longPathAware.Value.Trim());
    }

    private static XElement Named(string name) =>
        LoadMainWindow()
            .Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XDocument LoadMainWindow() =>
        XDocument.Load(Path.Combine(FindComponentRoot(), "src",
            "GameShift.MemoryOptimizer", "MainWindow.xaml"));

    internal static string FindComponentRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameShift.MemoryOptimizer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Memory Optimizer source root not found.");
    }
}

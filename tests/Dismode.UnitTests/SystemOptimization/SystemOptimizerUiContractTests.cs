using System.Xml.Linq;

namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerUiContractTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ExpectedDestinations =
    [
        "overview", "hardware", "games", "experiments",
        "global", "laboratory", "danger", "history",
    ];

    private static readonly string[] ExpectedPages =
    [
        "OverviewPage", "HardwarePage", "GamesPage", "ExperimentsPage",
        "GlobalPage", "LaboratoryPage", "DangerPage", "HistoryPage",
    ];

    [TestMethod]
    public void SeparateOptimizerUsesStableResponsiveNavigationShell()
    {
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));
        XElement navigation = Named(window, "OptimizerNavigation");
        string[] destinations = navigation.Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .Select(element => (string?)element.Attribute("Tag"))
            .Where(static tag => tag is not null)
            .Select(static tag => tag!)
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedDestinations,
            destinations);
        Assert.AreEqual("Auto", (string?)navigation.Attribute("PaneDisplayMode"));
        Assert.AreEqual("True", (string?)navigation.Attribute("IsPaneOpen"));

        foreach (string pageName in ExpectedPages)
        {
            XElement page = Named(window, pageName);
            Assert.AreEqual("Disabled", (string?)page.Attribute("HorizontalScrollMode"));
            Assert.AreEqual(
                pageName == "OverviewPage" ? null : "Collapsed",
                (string?)page.Attribute("Visibility"));
        }
    }

    [TestMethod]
    public void WindowUsesDarkExtendedTitleBarAndStableProgressSurface()
    {
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.IsNotNull(Named(window, "TitleBarRoot"));
        XElement status = Named(window, "ServiceStatusStrip");
        Assert.AreEqual("64", (string?)status.Attribute("Height"));
        XElement progress = Named(window, "OperationProgressRing");
        Assert.AreEqual("Visible", (string?)progress.Attribute("Visibility"));
        Assert.AreEqual("0", (string?)progress.Attribute("Opacity"));

        string code = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        StringAssert.Contains(code, "ExtendsContentIntoTitleBar = true");
        StringAssert.Contains(code, "SetTitleBar(TitleBarDragRegion)");
        StringAssert.Contains(code, "MicaBackdrop");
        StringAssert.Contains(code, "AccessibilitySettings");
        StringAssert.Contains(code, "HighContrast");
        StringAssert.Contains(code, "titleBar.BackgroundColor = null");
    }

    [TestMethod]
    public void ManifestUsesPerMonitorV2DpiAwareness()
    {
        XDocument manifest = XDocument.Load(SourcePath("app.manifest"));
        XElement dpi = manifest.Descendants().Single(element =>
            element.Name.LocalName == "dpiAwareness");
        Assert.AreEqual("PerMonitorV2", dpi.Value.Trim());
    }

    [TestMethod]
    public void RepeatedLaunchBringsTheSingleExistingWindowForward()
    {
        string source = File.ReadAllText(SourcePath("App.xaml.cs"));
        string windowSource = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));

        StringAssert.Contains(source, "Local\\\\Dismode.SystemOptimizer");
        StringAssert.Contains(source, "EventWaitHandle");
        StringAssert.Contains(source, "ThreadPool.RegisterWaitForSingleObject");
        StringAssert.Contains(source, "_activationSignal.Set()");
        StringAssert.Contains(source, "SetForegroundWindow");
        Assert.IsFalse(source.Contains("FindWindow", StringComparison.Ordinal));
        StringAssert.Contains(windowSource, "ProductInformation.CurrentVersion");
        Assert.IsNotNull(Named(window, "TitleBarVersionText"));
        Assert.IsFalse(windowSource.Contains(
            "System Optimizer 0.4.0",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void BenchmarkPageUsesVerifiedActiveGameAndRawPresentMonCapture()
    {
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.IsNotNull(Named(window, "ActiveGameLeaseText"));
        Assert.IsNotNull(Named(window, "SafeTweakComboBox"));
        Assert.IsNotNull(Named(window, "PrepareExperimentButton"));
        Assert.IsNotNull(Named(window, "RunBenchmarkButton"));
        Assert.IsNotNull(Named(window, "ExperimentProgressBar"));
        Assert.IsNotNull(Named(window, "BenchmarkResultCard"));
        Assert.IsNotNull(Named(window, "BenchmarkVerdictText"));
        Assert.IsNotNull(Named(window, "BenchmarkPrimaryMetricText"));
        Assert.IsNotNull(Named(window, "BenchmarkAverageFpsText"));
        Assert.IsNotNull(Named(window, "BenchmarkHitchRateText"));
        Assert.IsNotNull(Named(window, "BenchmarkPairsText"));

        string code = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        StringAssert.Contains(code, "ActiveGameLeaseReader");
        StringAssert.Contains(code, "CreateBenchmarkProvider");
        StringAssert.Contains(code, "CaptureBenchmarkAsync");
        StringAssert.Contains(code, "AverageGpuBusyPercent");
        StringAssert.Contains(code, "SystemMemoryUsageReader");
        StringAssert.Contains(code, "SubmitCaptureAsync");
        StringAssert.Contains(code, "RenderBenchmarkDecision(");
        StringAssert.Contains(code, "status.ActiveExperiment is not null");
        StringAssert.Contains(
            code,
            "ExperimentStateValue.ExperimentStateCompleted");
        StringAssert.Contains(code, "clearCompletedExperiment: true");
    }

    [TestMethod]
    public void GamesPageLoadsAndRendersTheCurrentUsersSavedProfiles()
    {
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.IsNotNull(Named(window, "SavedGameProfilesList"));

        string windowCode = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        string clientCode = File.ReadAllText(
            SourcePath(Path.Combine("Services", "SystemOptimizerClient.cs")));

        StringAssert.Contains(windowCode, "RenderProfiles(");
        StringAssert.Contains(windowCode, "profilesTask");
        StringAssert.Contains(clientCode, "GetProfilesAsync(");
    }

    [TestMethod]
    public void DangerousGlobalExperimentRequiresPerItemTypedConsent()
    {
        XDocument window = XDocument.Load(SourcePath("MainWindow.xaml"));
        Assert.IsNotNull(Named(window, "DangerousTweakComboBox"));
        Assert.IsNotNull(Named(window, "DangerousConfirmationTextBox"));
        Assert.IsNotNull(Named(window, "PrepareDangerousExperimentButton"));
        Assert.IsNotNull(Named(window, "KeepGlobalResultButton"));

        string code = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        string clientCode = File.ReadAllText(
            SourcePath(Path.Combine("Services", "SystemOptimizerClient.cs")));
        StringAssert.Contains(code, "DangerousConfirmationText");
        StringAssert.Contains(code, "PrepareDangerousExperimentButton_Click");
        StringAssert.Contains(code, "ExperimentControlAction.KeepGlobally");
        StringAssert.Contains(clientCode, "DangerousConfirmation =");
    }

    [TestMethod]
    public void OperationRefreshRunsAfterTheBusyStateIsCleared()
    {
        string code = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        int start = code.IndexOf(
            "private async Task RunOperationAsync",
            StringComparison.Ordinal);
        int end = code.IndexOf(
            "private async Task<ActiveGameLease?>",
            start,
            StringComparison.Ordinal);
        string method = code[start..end];
        int busyCleared = method.IndexOf(
            "_operationInProgress = false",
            StringComparison.Ordinal);
        int refresh = method.LastIndexOf(
            "await RefreshAsync()",
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, busyCleared);
        Assert.IsGreaterThanOrEqualTo(0, refresh);
        Assert.IsTrue(busyCleared < refresh);
    }

    private static XElement Named(XDocument source, string name) =>
        source.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == name);

    private static string SourcePath(string fileName) =>
        Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Dismode.SystemOptimizer",
            fileName);

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

        throw new DirectoryNotFoundException("Dismode repository root not found.");
    }
}

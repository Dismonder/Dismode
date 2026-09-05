using System.Xml.Linq;
using GameShift.UI.Services;

namespace GameShift.UnitTests;

[TestClass]
public sealed class MemoryOptimizerIntegrationContractTests
{
    private static readonly string[] ForbiddenProjectNames =
    [
        "GameShift.Core",
        "GameShift.Data",
        "GameShift.Windows",
        "GameShift.Contracts",
    ];

    [TestMethod]
    public void GplProjectsDoNotReferenceGamingLibraries()
    {
        string componentRoot = Path.Combine(
            FindRepositoryRoot(),
            "components",
            "GameShift.MemoryOptimizer");
        string[] projectFiles =
        [
            Path.Combine(
                componentRoot,
                "src",
                "GameShift.MemoryOptimizer.Core",
                "GameShift.MemoryOptimizer.Core.csproj"),
            Path.Combine(
                componentRoot,
                "src",
                "GameShift.MemoryService",
                "GameShift.MemoryService.csproj"),
            Path.Combine(
                componentRoot,
                "src",
                "GameShift.MemoryOptimizer",
                "GameShift.MemoryOptimizer.csproj"),
        ];

        foreach (string projectFile in projectFiles)
        {
            string project = XDocument.Load(projectFile).ToString();
            foreach (string forbiddenProjectName in ForbiddenProjectNames)
            {
                Assert.IsFalse(
                    project.Contains(
                        $"{forbiddenProjectName}.csproj",
                        StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [TestMethod]
    public void InstallerTreatsMemoryOptimizerAsOptionalAggregate()
    {
        string repositoryRoot = FindRepositoryRoot();
        string installer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "installer",
            "GameShift.iss"));
        string componentInstaller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "tools",
            "Install-MemoryOptimizer.ps1"));

        StringAssert.Contains(installer, "#ifndef MemoryOptimizerDir");
        StringAssert.Contains(installer, "Name: \"memoryoptimizer\"");
        StringAssert.Contains(installer, "Components: memoryoptimizer");
        StringAssert.Contains(installer, "GNU GPL v3");
        StringAssert.Contains(
            installer,
            "MemoryOptimizer\\tools\\Install-MemoryOptimizer.ps1");
        StringAssert.Contains(
            installer,
            "PrepareMemoryOptimizerForUpdate");
        Assert.IsFalse(installer.Contains(
            "{username}",
            StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(componentInstaller, "GameShiftMemoryService");
        StringAssert.Contains(
            componentInstaller,
            "$definition.Principal.GroupId = \"S-1-5-4\"");
        StringAssert.Contains(
            componentInstaller,
            "$definition.Settings.MultipleInstances = 0");
    }

    [TestMethod]
    public void ShutdownHelperNeverTargetsMemoryOptimizerProcesses()
    {
        string helper = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GameShift.SessionHost",
            "SessionHostUpdatePreparation.cs"));
        string protocol = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "GameShift.Contracts",
            "Grpc",
            "gameshift.proto"));

        StringAssert.Contains(
            protocol,
            "rpc ShutdownComponents (ShutdownComponentsRequest)");
        StringAssert.Contains(helper, "bool stopUserInterface");
        Assert.IsFalse(helper.Contains(
            "GameShift.MemoryOptimizer",
            StringComparison.Ordinal));
        Assert.IsFalse(helper.Contains(
            "GameShiftMemoryService",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void RunningServiceWithoutTrayDoesNotClaimTheInterfaceIsRunning()
    {
        MemoryOptimizerComponentSnapshot snapshot =
            MemoryOptimizerComponentPresentation.CreateInstalledSnapshot(
                serviceRunning: true,
                trayRunning: false,
                paused: false);

        Assert.AreEqual(
            MemoryOptimizerComponentState.InterfaceStopped,
            snapshot.State);
        Assert.AreEqual("Interfejs zamknięty", snapshot.DisplayState);
        Assert.IsTrue(snapshot.CanOpen);
    }

    [TestMethod]
    public void NewlyStartedInterfaceThatExitsIsReportedAsLaunchFailure()
    {
        string? failure = MemoryOptimizerComponentPresentation.GetLaunchFailure(
            interfaceWasRunning: false,
            launchedProcessExited: true,
            exitCode: unchecked((int)0xC000027B));

        Assert.IsNotNull(failure);
        StringAssert.Contains(failure, "0xC000027B");
        Assert.IsNull(MemoryOptimizerComponentPresentation.GetLaunchFailure(
            interfaceWasRunning: false,
            launchedProcessExited: false,
            exitCode: 0));
    }

    [TestMethod]
    public void CompanionAppsReactivateThroughNamedSignalsInsteadOfWindowTitles()
    {
        string repositoryRoot = FindRepositoryRoot();
        string memoryApplication = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "App.xaml.cs"));
        string systemApplication = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "GameShift.SystemOptimizer",
            "App.xaml.cs"));

        foreach (string application in
            new[] { memoryApplication, systemApplication })
        {
            StringAssert.Contains(application, "EventWaitHandle");
            StringAssert.Contains(
                application,
                "ThreadPool.RegisterWaitForSingleObject");
            StringAssert.Contains(application, "_activationSignal.Set()");
            Assert.IsFalse(application.Contains(
                "FindWindow",
                StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void MemoryTrayReturnsAfterExplorerRestarts()
    {
        string trayService = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "TrayIconService.cs"));

        StringAssert.Contains(trayService, "TaskbarCreated");
        StringAssert.Contains(trayService, "RegisterWindowMessage");
        StringAssert.Contains(trayService, "RestoreAfterExplorerRestart");
    }

    [TestMethod]
    public void MemoryTrayUsesCustomMenuAndOffersPermanentDisable()
    {
        string repositoryRoot = FindRepositoryRoot();
        string trayService = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "TrayIconService.cs"));
        string menu = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "TrayMenuWindow.xaml"));
        string application = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "App.xaml.cs"));
        string shutdown = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "ComponentControl",
            "MemoryOptimizerComponentShutdown.cs"));

        StringAssert.Contains(trayService, "_showContextMenu");
        Assert.IsFalse(trayService.Contains(
            "TrackPopupMenuEx",
            StringComparison.Ordinal));
        StringAssert.Contains(menu, "Wyłącz Memory Optimizer");
        StringAssert.Contains(menu, "Zamknij interfejs");
        StringAssert.Contains(menu, "TrayMenuWindow");
        StringAssert.Contains(menu, "RamProgressBar");
        StringAssert.Contains(menu, "MemoryPercentText");
        StringAssert.Contains(menu, "Usługa działa niezależnie");
        string menuCode = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "TrayMenuWindow.xaml.cs"));
        StringAssert.Contains(menuCode, "ExitMenuButton");
        StringAssert.Contains(menuCode, "_exit();");
        StringAssert.Contains(menuCode, "MemoryPressureText");
        StringAssert.Contains(menuCode, "RamProgressBar.Value");
        StringAssert.Contains(application, "--disable-component");
        StringAssert.Contains(shutdown, "GameShiftMemoryService");
        StringAssert.Contains(shutdown, "GameShift Memory Optimizer");
        StringAssert.Contains(shutdown, "runas");
    }

    [TestMethod]
    public void BackgroundLaunchRemainsVisibleWhenTrayCreationFails()
    {
        string application = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "components",
            "GameShift.MemoryOptimizer",
            "src",
            "GameShift.MemoryOptimizer",
            "App.xaml.cs"));

        StringAssert.Contains(application, "TryCreateTray");
        StringAssert.Contains(
            application,
            "(startInBackground || startedAtLogon) && _tray is not null");
    }

    [TestMethod]
    public void GplReleaseRunsTheTrayStartupProbeBeforePublishingPayload()
    {
        string componentRoot = Path.Combine(
            FindRepositoryRoot(),
            "components",
            "GameShift.MemoryOptimizer");
        string buildScript = File.ReadAllText(Path.Combine(
            componentRoot,
            "tools",
            "Build-MemoryOptimizer.ps1"));
        string application = File.ReadAllText(Path.Combine(
            componentRoot,
            "src",
            "GameShift.MemoryOptimizer",
            "App.xaml.cs"));

        StringAssert.Contains(buildScript, "--startup-probe");
        StringAssert.Contains(application, "--startup-probe");
    }

    [TestMethod]
    public void MaintenanceUninstallDoesNotExpandAppBeforeItIsInitialized()
    {
        string installer = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "installer",
            "GameShift.iss"));
        int handlerStart = installer.IndexOf(
            "function NextButtonClick",
            StringComparison.Ordinal);
        int handlerEnd = installer.IndexOf(
            "function PrepareToInstall",
            handlerStart,
            StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, handlerStart);
        Assert.IsTrue(handlerEnd > handlerStart);
        string handler = installer[handlerStart..handlerEnd];
        Assert.IsFalse(handler.Contains(
            "ExpandConstant('{app}",
            StringComparison.Ordinal));
        StringAssert.Contains(handler, "ExistingInstallPath");
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

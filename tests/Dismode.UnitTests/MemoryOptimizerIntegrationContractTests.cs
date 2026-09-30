using System.Xml.Linq;
using Dismode.UI.Services;

namespace Dismode.UnitTests;

[TestClass]
public sealed class MemoryOptimizerIntegrationContractTests
{
    private static readonly string[] ForbiddenProjectNames =
    [
        "Dismode.Core",
        "Dismode.Data",
        "Dismode.Windows",
        "Dismode.Contracts",
    ];

    [TestMethod]
    public void GplProjectsDoNotReferenceGamingLibraries()
    {
        string componentRoot = Path.Combine(
            FindRepositoryRoot(),
            "components",
            "Dismode.MemoryOptimizer");
        string[] projectFiles =
        [
            Path.Combine(
                componentRoot,
                "src",
                "Dismode.MemoryOptimizer.Core",
                "Dismode.MemoryOptimizer.Core.csproj"),
            Path.Combine(
                componentRoot,
                "src",
                "Dismode.MemoryService",
                "Dismode.MemoryService.csproj"),
            Path.Combine(
                componentRoot,
                "src",
                "Dismode.MemoryOptimizer",
                "Dismode.MemoryOptimizer.csproj"),
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
            "Dismode.iss"));
        string componentInstaller = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "Dismode.MemoryOptimizer",
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
        StringAssert.Contains(componentInstaller, "DismodeMemoryService");
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
            "Dismode.SessionHost",
            "SessionHostUpdatePreparation.cs"));
        string protocol = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Dismode.Contracts",
            "Grpc",
            "dismode.proto"));

        StringAssert.Contains(
            protocol,
            "rpc ShutdownComponents (ShutdownComponentsRequest)");
        StringAssert.Contains(helper, "bool stopUserInterface");
        Assert.IsFalse(helper.Contains(
            "Dismode.MemoryOptimizer",
            StringComparison.Ordinal));
        Assert.IsFalse(helper.Contains(
            "DismodeMemoryService",
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
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
            "App.xaml.cs"));
        string systemApplication = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Dismode.SystemOptimizer",
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
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
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
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
            "TrayIconService.cs"));
        string menu = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
            "TrayMenuWindow.xaml"));
        string application = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
            "App.xaml.cs"));
        string shutdown = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "components",
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
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
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
            "TrayMenuWindow.xaml.cs"));
        StringAssert.Contains(menuCode, "ExitMenuButton");
        StringAssert.Contains(menuCode, "_exit();");
        StringAssert.Contains(menuCode, "MemoryPressureText");
        StringAssert.Contains(menuCode, "RamProgressBar.Value");
        StringAssert.Contains(application, "--disable-component");
        StringAssert.Contains(shutdown, "DismodeMemoryService");
        StringAssert.Contains(shutdown, "Dismode Memory Optimizer");
        StringAssert.Contains(shutdown, "runas");
    }

    [TestMethod]
    public void BackgroundLaunchRemainsVisibleWhenTrayCreationFails()
    {
        string application = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "components",
            "Dismode.MemoryOptimizer",
            "src",
            "Dismode.MemoryOptimizer",
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
            "Dismode.MemoryOptimizer");
        string buildScript = File.ReadAllText(Path.Combine(
            componentRoot,
            "tools",
            "Build-MemoryOptimizer.ps1"));
        string application = File.ReadAllText(Path.Combine(
            componentRoot,
            "src",
            "Dismode.MemoryOptimizer",
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
            "Dismode.iss"));
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
            "Nie znaleziono katalogu repozytorium Dismode.");
    }
}

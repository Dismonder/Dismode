namespace GameShift.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerReleaseContractTests
{
    [TestMethod]
    public void ReleasePublishesSeparateOptimizerPayload()
    {
        string script = ReadRepositoryFile("tools", "Build-LocalRelease.ps1");

        StringAssert.Contains(
            script,
            "src\\GameShift.SystemOptimizer\\GameShift.SystemOptimizer.csproj");
        StringAssert.Contains(script, "SystemOptimizer\\GameShift.SystemOptimizer.exe");
        StringAssert.Contains(script, "$systemOptimizerStagingPath");
    }

    [TestMethod]
    public void InstallerConfiguresDelayedAutomaticSystemAgentService()
    {
        string installer = ReadRepositoryFile("installer", "GameShift.iss");
        string serviceInstaller = ReadRepositoryFile(
            "installer",
            "system-agent",
            "Install-SystemAgent.ps1");

        StringAssert.Contains(installer, "InstallSystemAgentService");
        StringAssert.Contains(serviceInstaller, "start= delayed-auto");
        StringAssert.Contains(serviceInstaller, "obj= LocalSystem");
        StringAssert.Contains(installer, "SystemOptimizer\\GameShift.SystemOptimizer.exe");
    }

    [TestMethod]
    public void InstallerFailsClosedWhenSystemAgentServiceScriptsFail()
    {
        string installer = ReadRepositoryFile("installer", "GameShift.iss");

        StringAssert.Contains(installer, "InstallSystemAgentService");
        StringAssert.Contains(installer, "UninstallSystemAgentService");
        StringAssert.Contains(installer, "ResultCode <> 0");
        StringAssert.Contains(installer, "CustomSetupExitCode := 21");
        Assert.IsFalse(ExtractSection(installer, "Run").Contains(
            "SystemIntegration\\system-agent\\Install-SystemAgent.ps1",
            StringComparison.Ordinal));
        Assert.IsFalse(ExtractSection(installer, "UninstallRun").Contains(
            "SystemIntegration\\system-agent\\Uninstall-SystemAgent.ps1",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void PreviewUpgradeFloorIsVersionZeroThree()
    {
        string script = ReadRepositoryFile("tools", "Build-Installer.ps1");

        StringAssert.Contains(script, "\"--minimum-version\",");
        StringAssert.Contains(script, "\"0.3.0\",");
        StringAssert.Contains(script, "GameShift $Version Gaming Edition");
    }

    [TestMethod]
    public void MaintenanceChecksOptimizerRecoveryWithoutKillingTheServiceProcess()
    {
        string source = ReadRepositoryFile(
            "src",
            "GameShift.SessionHost",
            "SessionHostUpdatePreparation.cs");

        StringAssert.Contains(source, "PrepareForUpdateAsync");
        Assert.IsFalse(source.Contains(
            "GameShift.SystemAgent.exe",
            StringComparison.Ordinal));
        StringAssert.Contains(source, "GameShift.SystemOptimizer.exe");
    }

    [TestMethod]
    public void UninstallRestoresMachineTweaksBeforeRemovingSystemAgent()
    {
        string program = ReadRepositoryFile(
            "src",
            "GameShift.SessionHost",
            "Program.cs");
        string maintenance = ReadRepositoryFile(
            "src",
            "GameShift.SessionHost",
            "SessionHostUpdatePreparation.cs");
        string client = ReadRepositoryFile(
            "src",
            "GameShift.Windows",
            "Sessions",
            "SystemOptimizerGameProfileClient.cs");
        string installer = ReadRepositoryFile("installer", "GameShift.iss");

        StringAssert.Contains(program, "--prepare-uninstall");
        StringAssert.Contains(maintenance, "RestoreAllAsync");
        StringAssert.Contains(client, "SystemRestoreTarget.AllGameshiftChanges");
        StringAssert.Contains(installer, "'--prepare-uninstall'");

        string uninstall = ExtractUninstallRoutine(installer);

        // Brama restore musi wykonać się przed usunięciem usługi.
        int gateIndex = uninstall.IndexOf(
            "'--prepare-uninstall'",
            StringComparison.Ordinal);
        int serviceIndex = uninstall.IndexOf(
            "UninstallSystemAgentService",
            StringComparison.Ordinal);
        Assert.IsTrue(
            gateIndex >= 0,
            "Deinstalacja musi uruchomić bramę recovery.");
        Assert.IsTrue(
            serviceIndex > gateIndex,
            "Usługa musi zostać usunięta dopiero po bramie recovery.");

        // Nieudany restore nie może zablokować deinstalacji na stałe:
        // użytkownik musi dostać świadomy wybór zamiast ślepego zaułka.
        StringAssert.Contains(
            uninstall,
            "MB_YESNO",
            "Nieudana brama musi dawać wybór, a nie tylko komunikat błędu.");

        // Usunięcie usługi nie może być uzależnione od powodzenia bramy —
        // inaczej nieudany restore zostawia usługę na autostarcie.
        Assert.IsFalse(
            uninstall.Contains(
                "if Result then\n    Result := UninstallSystemAgentService",
                StringComparison.Ordinal),
            "Usunięcie usługi nie może być schowane za powodzeniem bramy.");
    }

    private static string ExtractUninstallRoutine(string installer)
    {
        const string start = "function InitializeUninstall: Boolean;";
        const string end = "procedure CurStepChanged";
        int from = installer.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(from >= 0, "Brak funkcji InitializeUninstall.");
        int to = installer.IndexOf(end, from, StringComparison.Ordinal);
        Assert.IsTrue(to > from, "Nie udało się wyznaczyć końca funkcji.");
        return installer[from..to];
    }

    [TestMethod]
    public void ReleaseRequiresProductionSigningAndDeploysTrustedSignerPolicy()
    {
        string localRelease = ReadRepositoryFile(
            "tools",
            "Build-LocalRelease.ps1");
        string installerBuild = ReadRepositoryFile(
            "tools",
            "Build-Installer.ps1");
        string installer = ReadRepositoryFile("installer", "GameShift.iss");

        StringAssert.Contains(localRelease, "CodeSigningCertificateThumbprint");
        StringAssert.Contains(localRelease, "trusted-signers.json");
        StringAssert.Contains(localRelease, "GameShift.SessionHost.exe");
        StringAssert.Contains(localRelease, "GameShift.SystemOptimizer.exe");
        StringAssert.Contains(installerBuild, "GAMESHIFT_RELEASE_SIGNING_THUMBPRINT");
        StringAssert.Contains(installerBuild, "Get-AuthenticodeSignature");
        StringAssert.Contains(installerBuild, "Wydanie $Version wymaga");
        StringAssert.Contains(installer, "trusted-signers.json");
    }

    [TestMethod]
    public void LocalInstallerCanExplicitlyUseADevelopmentSigningCertificate()
    {
        string localRelease = ReadRepositoryFile(
            "tools",
            "Build-LocalRelease.ps1");
        string installerBuild = ReadRepositoryFile(
            "tools",
            "Build-Installer.ps1");

        StringAssert.Contains(
            installerBuild,
            "[switch]$AllowTestCodeSigningCertificate");
        StringAssert.Contains(
            installerBuild,
            "AllowTestCodeSigningCertificate =");
        StringAssert.Contains(
            localRelease,
            "EnhancedKeyUsageList.ObjectId -contains $codeSigningOid");
        Assert.IsFalse(localRelease.Contains(
            "EnhancedKeyUsageList.Value",
            StringComparison.Ordinal));
        StringAssert.Contains(
            localRelease,
            "if (-not $AllowTestCodeSigningCertificate -and");
    }

    [TestMethod]
    public void InstallerSignsMemoryOptimizerAndExpandsItsReleaseNotice()
    {
        string installerBuild = ReadRepositoryFile(
            "tools",
            "Build-Installer.ps1");
        string memoryBuild = ReadRepositoryFile(
            "components",
            "GameShift.MemoryOptimizer",
            "tools",
            "Build-MemoryOptimizer.ps1");
        string releaseGate = ReadRepositoryFile(
            "components",
            "GameShift.MemoryOptimizer",
            "tools",
            "Test-GplRelease.ps1");
        string notice = ReadRepositoryFile(
            "components",
            "GameShift.MemoryOptimizer",
            "NOTICE.md");

        StringAssert.Contains(
            installerBuild,
            "$memoryOptimizerArguments.CodeSigningCertificateThumbprint");
        StringAssert.Contains(
            memoryBuild,
            "[string]$CodeSigningCertificateThumbprint");
        StringAssert.Contains(
            memoryBuild,
            "[switch]$AllowTestCodeSigningCertificate");
        StringAssert.Contains(
            installerBuild,
            "$memoryOptimizerArguments.AllowTestCodeSigningCertificate");
        StringAssert.Contains(memoryBuild, "Get-AuthenticodeSignature");
        StringAssert.Contains(memoryBuild, "{{PRODUCT_VERSION}}");
        StringAssert.Contains(releaseGate, "$sourceArchiveName");
        StringAssert.Contains(releaseGate, "NOTICE.md does not name");
        StringAssert.Contains(notice, "{{PRODUCT_VERSION}}");
        Assert.IsTrue(notice.StartsWith(
            "# GameShift Memory Optimizer {{PRODUCT_VERSION}} ",
            StringComparison.Ordinal));
        Assert.IsFalse(notice.Contains(
            "0.3.0-source.zip",
            StringComparison.Ordinal));
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine(
            new[] { FindRepositoryRoot() }.Concat(segments).ToArray()));

    private static string ExtractSection(string content, string sectionName)
    {
        string marker = $"[{sectionName}]";
        int start = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        int end = content.IndexOf(
            "\n[",
            start + marker.Length,
            StringComparison.Ordinal);
        return end < 0 ? content[start..] : content[start..end];
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("GameShift repository root not found.");
    }
}

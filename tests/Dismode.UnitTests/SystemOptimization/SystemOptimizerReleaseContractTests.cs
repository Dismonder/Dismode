namespace Dismode.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemOptimizerReleaseContractTests
{
    [TestMethod]
    public void ReleasePublishesSeparateOptimizerPayload()
    {
        string script = ReadRepositoryFile("tools", "Build-LocalRelease.ps1");

        StringAssert.Contains(
            script,
            "src\\Dismode.SystemOptimizer\\Dismode.SystemOptimizer.csproj");
        StringAssert.Contains(script, "SystemOptimizer\\Dismode.SystemOptimizer.exe");
        StringAssert.Contains(script, "$systemOptimizerStagingPath");
    }

    [TestMethod]
    public void InstallerConfiguresDelayedAutomaticSystemAgentService()
    {
        string installer = ReadRepositoryFile("installer", "Dismode.iss");
        string serviceInstaller = ReadRepositoryFile(
            "installer",
            "system-agent",
            "Install-SystemAgent.ps1");

        StringAssert.Contains(installer, "InstallSystemAgentService");
        StringAssert.Contains(serviceInstaller, "start= delayed-auto");
        StringAssert.Contains(serviceInstaller, "obj= LocalSystem");
        StringAssert.Contains(installer, "SystemOptimizer\\Dismode.SystemOptimizer.exe");
    }

    [TestMethod]
    public void InstallerHardensTheMachineDataDirectoryBeforeStartingTheAgent()
    {
        // Usluga LocalSystem czyta z tego katalogu dziennik, baze i liste
        // zaufanych podpisow. Odziedziczony ACL ProgramData pozwala kazdemu
        // tworzyc tam pliki, wiec instalator musi go zastapic wlasnym, a
        // Shared zostawic do zapisu dla interfejsu bez uprawnien.
        string serviceInstaller = ReadRepositoryFile(
            "installer",
            "system-agent",
            "Install-SystemAgent.ps1");

        StringAssert.Contains(serviceInstaller, "/setowner \"*S-1-5-32-544\"");
        StringAssert.Contains(serviceInstaller, "/inheritance:r");
        StringAssert.Contains(serviceInstaller, "\"*S-1-5-18:(OI)(CI)F\"");
        StringAssert.Contains(serviceInstaller, "\"*S-1-5-32-544:(OI)(CI)F\"");
        StringAssert.Contains(serviceInstaller, "\"*S-1-5-32-545:(OI)(CI)RX\"");
        StringAssert.Contains(serviceInstaller, "\"*S-1-5-32-545:(OI)(CI)M\"");
        int hardening = serviceInstaller.IndexOf(
            "/inheritance:r",
            StringComparison.Ordinal);
        int start = serviceInstaller.IndexOf(
            "Start-Service -Name $serviceName",
            StringComparison.Ordinal);
        Assert.IsTrue(
            hardening >= 0 && start > hardening,
            "Uprawnienia musza byc ustawione, zanim usluga wystartuje.");
    }

    [TestMethod]
    public void InstallerFailsClosedWhenSystemAgentServiceScriptsFail()
    {
        string installer = ReadRepositoryFile("installer", "Dismode.iss");

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
        StringAssert.Contains(script, "Dismode $Version Gaming Edition");
    }

    [TestMethod]
    public void MaintenanceChecksOptimizerRecoveryWithoutKillingTheServiceProcess()
    {
        string source = ReadRepositoryFile(
            "src",
            "Dismode.SessionHost",
            "SessionHostUpdatePreparation.cs");

        StringAssert.Contains(source, "PrepareForUpdateAsync");
        Assert.IsFalse(source.Contains(
            "Dismode.SystemAgent.exe",
            StringComparison.Ordinal));
        StringAssert.Contains(source, "Dismode.SystemOptimizer.exe");
    }

    [TestMethod]
    public void UninstallRestoresMachineTweaksBeforeRemovingSystemAgent()
    {
        string program = ReadRepositoryFile(
            "src",
            "Dismode.SessionHost",
            "Program.cs");
        string maintenance = ReadRepositoryFile(
            "src",
            "Dismode.SessionHost",
            "SessionHostUpdatePreparation.cs");
        string client = ReadRepositoryFile(
            "src",
            "Dismode.Windows",
            "Sessions",
            "SystemOptimizerGameProfileClient.cs");
        string installer = ReadRepositoryFile("installer", "Dismode.iss");

        StringAssert.Contains(program, "--prepare-uninstall");
        StringAssert.Contains(maintenance, "RestoreAllAsync");
        StringAssert.Contains(client, "SystemRestoreTarget.AllDismodeChanges");
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
        string installer = ReadRepositoryFile("installer", "Dismode.iss");

        StringAssert.Contains(localRelease, "CodeSigningCertificateThumbprint");
        StringAssert.Contains(localRelease, "trusted-signers.json");
        StringAssert.Contains(localRelease, "Dismode.SessionHost.exe");
        StringAssert.Contains(localRelease, "Dismode.SystemOptimizer.exe");
        StringAssert.Contains(installerBuild, "DISMODE_RELEASE_SIGNING_THUMBPRINT");
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
            "Dismode.MemoryOptimizer",
            "tools",
            "Build-MemoryOptimizer.ps1");
        string releaseGate = ReadRepositoryFile(
            "components",
            "Dismode.MemoryOptimizer",
            "tools",
            "Test-GplRelease.ps1");
        string notice = ReadRepositoryFile(
            "components",
            "Dismode.MemoryOptimizer",
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
            "# Dismode Memory Optimizer {{PRODUCT_VERSION}} ",
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
            if (File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Dismode repository root not found.");
    }
}

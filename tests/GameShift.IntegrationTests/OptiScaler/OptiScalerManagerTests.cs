using System.Text;
using GameShift.Core.OptiScaler;
using GameShift.Windows.OptiScaler;

namespace GameShift.IntegrationTests.OptiScaler;

[TestClass]
public sealed class OptiScalerManagerTests
{
    private const string TinySevenZipFixture =
        "N3q8ryccAAR97wngCwAAAAAAAABaAAAAAAAAAHNSDhcBAAZEYW1pYW4KAAEEBgABCQsA"
        + "BwsBAAEhIQEADAcACAoBvleymwAABQEZDAAAAAAAAAAAAAAAABETAGgAbwBzAHQAbgBh"
        + "AG0AZQAAABkAFAoBAFGoiPyxON0BFQYBACCApIEAAA==";

    [TestMethod]
    public async Task SharpCompressExtractorReadsSevenZipPayload()
    {
        using OptiScalerTestContext context = new();
        string archive = Path.Combine(context.PayloadDirectory, "fixture.7z");
        await File.WriteAllBytesAsync(
            archive,
            Convert.FromBase64String(TinySevenZipFixture));
        string extraction = Path.Combine(context.PayloadDirectory, "Extracted");

        await new SharpCompressOptiScalerArchiveExtractor().ExtractAsync(
            archive,
            extraction,
            CancellationToken.None);

        Assert.AreEqual(
            "Damian\n",
            await File.ReadAllTextAsync(Path.Combine(extraction, "hostname")));
    }

    [TestMethod]
    public async Task InstallAndRemoveRoundTripRestoresOriginalProxy()
    {
        using OptiScalerTestContext context = new();
        string launcher = context.CreateGameFile("Game.exe", "launcher");
        string shipping = context.CreateGameFile(
            Path.Combine("Binaries", "Win64", "Game-Win64-Shipping.exe"),
            "game");
        string originalProxy = context.CreateGameFile(
            Path.Combine("Binaries", "Win64", "dxgi.dll"),
            "original proxy");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadFile("OptiScaler.ini", "[OptiScaler]");
        context.CreatePayloadFile(Path.Combine("Licenses", "LICENSE"), "GPL-3.0");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult installed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-1",
                launcher,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(installed.Succeeded, installed.Message);
        Assert.AreEqual(shipping, installed.TargetExecutablePath);
        Assert.AreEqual(
            "optiscaler proxy",
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(shipping)!, "dxgi.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(shipping)!, "OptiScaler.dll")));
        Assert.IsTrue(manager.GetStatus("profile-1").IsInstalled);

        OptiScalerOperationResult removed = await manager.RemoveAsync(
            "profile-1",
            CancellationToken.None);

        Assert.IsTrue(removed.Succeeded, removed.Message);
        Assert.AreEqual("original proxy", File.ReadAllText(originalProxy));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(shipping)!, "OptiScaler.ini")));
        Assert.IsFalse(manager.GetStatus("profile-1").IsInstalled);
    }

    [TestMethod]
    public async Task RemoveRefusesToDeleteAFileModifiedAfterInstallation()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "installed proxy");
        context.CreatePayloadFile("OptiScaler.ini", "installed settings");
        OptiScalerManager manager = context.CreateManager();
        OptiScalerOperationResult installed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-conflict",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);
        Assert.IsTrue(installed.Succeeded, installed.Message);
        string proxy = Path.Combine(context.GameDirectory, "dxgi.dll");
        File.WriteAllText(proxy, "changed by another tool");

        OptiScalerOperationResult removed = await manager.RemoveAsync(
            "profile-conflict",
            CancellationToken.None);

        Assert.IsFalse(removed.Succeeded);
        CollectionAssert.Contains(removed.ConflictingPaths.ToList(), proxy);
        Assert.AreEqual("changed by another tool", File.ReadAllText(proxy));
        Assert.IsTrue(manager.GetStatus("profile-conflict").IsInstalled);
    }

    [TestMethod]
    public async Task AntiCheatMarkerBlocksBeforePackageDownload()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreateGameFile(
            Path.Combine("EasyAntiCheat", "EasyAntiCheat_EOS.exe"),
            "anti-cheat");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-online",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.AntiCheatDetected,
            result.BlockReason);
        Assert.AreEqual(0, context.PackageSource.DownloadCount);
    }

    [TestMethod]
    public async Task ExperimentalChannelRequiresSeparateConfirmationBeforeDownload()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "installed proxy");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-beta-warning",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true,
                OptiScalerReleaseChannel.Beta,
                Version: "0.9.5-pre3",
                ExperimentalUseConfirmed: false),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.ExperimentalUseNotConfirmed,
            result.BlockReason);
        Assert.AreEqual(0, context.PackageSource.DownloadCount);
    }

    [TestMethod]
    public async Task InstallUsesExactVersionFromSelectedChannel()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "installed proxy");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-beta-version",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true,
                OptiScalerReleaseChannel.Beta,
                Version: "0.9.5-pre2",
                ExperimentalUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(OptiScalerReleaseChannel.Beta, context.PackageSource.RequestedChannel);
        Assert.AreEqual("0.9.5-pre2", context.PackageSource.DownloadedVersion);
        OptiScalerInstallationStatus status = manager.GetStatus("profile-beta-version");
        Assert.AreEqual(OptiScalerReleaseChannel.Beta, status.Channel);
        Assert.AreEqual("0.9.5-pre2", status.Version);
    }

    [TestMethod]
    public async Task AvailableVersionsAreCachedPerChannel()
    {
        using OptiScalerTestContext context = new();
        OptiScalerManager manager = context.CreateManager();

        _ = await manager.GetAvailableVersionsAsync(
            OptiScalerReleaseChannel.Beta,
            CancellationToken.None);
        _ = await manager.GetAvailableVersionsAsync(
            OptiScalerReleaseChannel.Beta,
            CancellationToken.None);
        _ = await manager.GetAvailableVersionsAsync(
            OptiScalerReleaseChannel.Stable,
            CancellationToken.None);

        Assert.AreEqual(2, context.PackageSource.AvailableRequestCount);
    }

    [TestMethod]
    public async Task InstallReusesMetadataLoadedForVersionSelection()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "installed proxy");
        OptiScalerManager manager = context.CreateManager();
        _ = await manager.GetAvailableVersionsAsync(
            OptiScalerReleaseChannel.Beta,
            CancellationToken.None);

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-cached-beta",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true,
                OptiScalerReleaseChannel.Beta,
                Version: "0.9.5-pre3",
                ExperimentalUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(1, context.PackageSource.AvailableRequestCount);
    }

    [TestMethod]
    public async Task InvalidPayloadLeavesGameDirectoryUntouched()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        string original = context.CreateGameFile("dxgi.dll", "original");
        context.CreatePayloadFile("OptiScaler.ini", "missing main dll");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-invalid",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("original", File.ReadAllText(original));
        Assert.IsFalse(manager.GetStatus("profile-invalid").IsInstalled);
    }

    [TestMethod]
    public async Task NetworkTimeoutReturnsReadableFailureWithoutTouchingGame()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        OptiScalerManager manager = context.CreateManager(
            new TimeoutPackageSource());

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-timeout",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "Przekroczono czas");
        Assert.IsFalse(File.Exists(Path.Combine(context.GameDirectory, "dxgi.dll")));
        Assert.IsFalse(manager.GetStatus("profile-timeout").IsInstalled);
    }

    [TestMethod]
    public async Task InstallRefusesReparsePointInsideGameDirectory()
    {
        using OptiScalerTestContext context = new();
        string executable = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "installed proxy");
        context.CreatePayloadFile(
            Path.Combine("Licenses", "LICENSE"),
            "GPL-3.0");
        Directory.CreateDirectory(context.OutsideDirectory);
        string linkedDirectory = Path.Combine(context.GameDirectory, "Licenses");
        try
        {
            Directory.CreateSymbolicLink(
                linkedDirectory,
                context.OutsideDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
            or PlatformNotSupportedException
            or IOException)
        {
            Assert.Inconclusive($"System testowy nie obsługuje symlinków: {exception.Message}");
        }

        OptiScalerManager manager = context.CreateManager();
        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-reparse",
                executable,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(File.Exists(Path.Combine(context.OutsideDirectory, "LICENSE")));
        Assert.IsFalse(File.Exists(Path.Combine(context.GameDirectory, "dxgi.dll")));
        Assert.IsFalse(manager.GetStatus("profile-reparse").IsInstalled);
    }

    [TestMethod]
    public void TargetResolverPrefersGameShippingExecutableButIgnoresEngineTools()
    {
        using OptiScalerTestContext context = new();
        string launcher = context.CreateGameFile("Game.exe", "launcher");
        _ = context.CreateGameFile(
            Path.Combine("Engine", "Binaries", "Win64", "Tool-Win64-Shipping.exe"),
            "tool");
        string game = context.CreateGameFile(
            Path.Combine("Game", "Binaries", "Win64", "Game-Win64-Shipping.exe"),
            "game");

        Assert.AreEqual(
            game,
            OptiScalerManager.ResolveTargetExecutable(
                launcher,
                context.GameDirectory));
    }

    [TestMethod]
    public async Task NeuralRenderingInstallsDriverModelAndEnablesTheIniSwitch()
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadFile("nvngx.dll_dlssnr.dll", "shim");
        context.CreatePayloadIni(withBom: true, complete: true);
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore(
            modelFileNames: ["nvngx_dlss.dll", "nvngx_dlssnr.dll"]);
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        OptiScalerOperationResult installed = await manager.InstallAsync(
            NeuralRequest(game, context),
            CancellationToken.None);

        Assert.IsTrue(installed.Succeeded, installed.Message);

        byte[] installedIni = File.ReadAllBytes(
            Path.Combine(context.GameDirectory, "OptiScaler.ini"));
        Assert.AreEqual<byte>(0xEF, installedIni[0], "BOM musi przetrwac patch.");
        string text = new UTF8Encoding(false).GetString(
            installedIni,
            3,
            installedIni.Length - 3);
        StringAssert.Contains(text, "Dx12Upscaler=dlss\r\n");
        StringAssert.Contains(text, "Dx11Upscaler=dlss_12\r\n");
        StringAssert.Contains(text, "[DlssNr]\r\nEnabled=true");

        Assert.AreEqual(
            "driver nvngx_dlssnr.dll",
            File.ReadAllText(
                Path.Combine(context.GameDirectory, "nvngx_dlssnr.dll")));
        Assert.AreEqual(
            "driver nvngx_dlss.dll",
            File.ReadAllText(
                Path.Combine(context.GameDirectory, "nvngx_dlss.dll")));
        Assert.IsTrue(File.Exists(
            Path.Combine(context.GameDirectory, "nvngx.dll_dlssnr.dll")));
    }

    [TestMethod]
    public async Task RemovingANeuralRenderingInstallCleansTheGameDirectory()
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore(
            modelFileNames: ["nvngx_dlssnr.dll"]);
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        Assert.IsTrue((await manager.InstallAsync(
            NeuralRequest(game, context),
            CancellationToken.None)).Succeeded);

        OptiScalerOperationResult removed = await manager.RemoveAsync(
            "profile-nr",
            CancellationToken.None);

        Assert.IsTrue(removed.Succeeded, removed.Message);
        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "nvngx_dlssnr.dll")));
        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "OptiScaler.ini")));
    }

    [TestMethod]
    public async Task NeuralRenderingIsRefusedOnNonGeForceHardware()
    {
        OptiScalerOperationResult result = await RunBlockedNeuralInstallAsync(
            context => context.CreateDriverStore(
                isGeForce: false,
                generation: GeForceGeneration.Unknown,
                modelFileNames: []));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GpuNotSupported,
            result.BlockReason);
    }

    [TestMethod]
    public async Task NeuralRenderingIsRefusedOnAnOlderGeneration()
    {
        OptiScalerOperationResult result = await RunBlockedNeuralInstallAsync(
            context => context.CreateDriverStore(
                generation: GeForceGeneration.Rtx40,
                modelFileNames: ["nvngx_dlssnr.dll"]));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.GpuNotSupported,
            result.BlockReason);
    }

    [TestMethod]
    public async Task NeuralRenderingIsRefusedOnAnOutdatedDriver()
    {
        OptiScalerOperationResult result = await RunBlockedNeuralInstallAsync(
            context => context.CreateDriverStore(
                driver: new NvidiaDriverVersion(616, 55),
                modelFileNames: ["nvngx_dlssnr.dll"]));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.DriverTooOld,
            result.BlockReason);
    }

    [TestMethod]
    public async Task NeuralRenderingIsRefusedWhenTheDriverHasNoModel()
    {
        OptiScalerOperationResult result = await RunBlockedNeuralInstallAsync(
            context => context.CreateDriverStore(
                modelFileNames: ["nvngx_dlss.dll"]));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.NeuralRenderingModelMissing,
            result.BlockReason);
    }

    [TestMethod]
    public async Task APackageWithoutTheNeuralRenderingKeysIsRejected()
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadIni(withBom: true, complete: false);
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore(
            modelFileNames: ["nvngx_dlssnr.dll"]);
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        OptiScalerOperationResult result = await manager.InstallAsync(
            NeuralRequest(game, context),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "DlssNr");

        // Nothing may be left behind after a refused install.
        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "nvngx_dlssnr.dll")));
        Assert.IsFalse(manager.GetStatus("profile-nr").IsInstalled);
    }

    [TestMethod]
    public async Task ADisabledNeuralRenderingRequestNeverProbesTheDriver()
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        bool probed = false;
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () =>
            {
                probed = true;
                return context.CreateDriverStore(modelFileNames: []);
            });

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-plain",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(probed);

        // The INI must stay exactly as the package shipped it.
        StringAssert.Contains(
            File.ReadAllText(
                Path.Combine(context.GameDirectory, "OptiScaler.ini")),
            "Dx12Upscaler=auto");
    }

    [TestMethod]
    public async Task NestedUpscalerBackendsFromTheForkLayoutAreInstalled()
    {
        // Oficjalna paczka trzyma D3D12_Optiscaler w katalogu glownym, a fork
        // Neural Rendering zagniezdza wszystkie backendy w OptiScaler\.
        // Lista dozwolonych katalogow po cichu gubila ten drugi uklad razem
        // z okolo 180 MB runtime'u, wiec instalacja konczyla sie sukcesem,
        // a mod byl niekompletny.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadFile("nvngx.dll_dlssnr.dll", "shim");
        context.CreatePayloadFile(
            Path.Combine("OptiScaler", "libxess.dll"),
            "xess");
        context.CreatePayloadFile(
            Path.Combine("OptiScaler", "amd_fidelityfx_upscaler_dx12.dll"),
            "fsr");
        context.CreatePayloadFile(
            Path.Combine("OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll"),
            "d3d12");
        context.CreatePayloadFile(
            Path.Combine("Licenses", "XeSS_LICENSE.txt"),
            "licencja");
        context.CreatePayloadFile("setup_windows.bat", "echo setup");
        context.CreatePayloadFile("!! EXTRACT ALL FILES TO GAME FOLDER !!", "");
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult installed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-layout",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(installed.Succeeded, installed.Message);

        // Kazdy backend musi trafic do gry z zachowaniem sciezki wzglednej.
        Assert.AreEqual(
            "xess",
            File.ReadAllText(Path.Combine(
                context.GameDirectory, "OptiScaler", "libxess.dll")));
        Assert.AreEqual(
            "fsr",
            File.ReadAllText(Path.Combine(
                context.GameDirectory,
                "OptiScaler",
                "amd_fidelityfx_upscaler_dx12.dll")));
        Assert.AreEqual(
            "d3d12",
            File.ReadAllText(Path.Combine(
                context.GameDirectory,
                "OptiScaler",
                "D3D12_OptiScaler",
                "D3D12Core.dll")));

        // Licencje zostaja - payload jest na GPL.
        Assert.IsTrue(File.Exists(Path.Combine(
            context.GameDirectory, "Licenses", "XeSS_LICENSE.txt")));

        // Skrypty instalacyjne i znacznik rozpakowania nie naleza do gry.
        Assert.IsFalse(File.Exists(Path.Combine(
            context.GameDirectory, "setup_windows.bat")));
        Assert.IsFalse(File.Exists(Path.Combine(
            context.GameDirectory, "!! EXTRACT ALL FILES TO GAME FOLDER !!")));

        // Deinstalacja musi posprzatac takze zagniezdzone katalogi.
        Assert.IsTrue((await manager.RemoveAsync(
            "profile-layout",
            CancellationToken.None)).Succeeded);
        Assert.IsFalse(Directory.Exists(Path.Combine(
            context.GameDirectory, "OptiScaler")));
    }

    [TestMethod]
    public async Task InstallReportsEveryStageInOrder()
    {
        // Bez raportowania etapow okno stoi nieme przez pobranie pakietu
        // i skopiowanie kilkuset megabajtow.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        RecordingProgress progress = new();
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-progress",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            progress,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);

        OptiScalerInstallStage[] stages = progress.Reports
            .Select(report => report.Stage)
            .ToArray();
        CollectionAssert.Contains(stages, OptiScalerInstallStage.Preparing);
        CollectionAssert.Contains(stages, OptiScalerInstallStage.Downloading);
        CollectionAssert.Contains(stages, OptiScalerInstallStage.Extracting);
        CollectionAssert.Contains(stages, OptiScalerInstallStage.BackingUp);
        CollectionAssert.Contains(stages, OptiScalerInstallStage.CopyingFiles);
        Assert.AreEqual(
            OptiScalerInstallStage.Completed,
            stages[^1],
            "Ostatni raport musi oznaczac zakonczenie.");

        // Etapy nie moga sie cofac - uzytkownik czytalby to jako blad.
        int[] ordinals = stages.Select(stage => (int)stage).ToArray();
        for (int index = 1; index < ordinals.Length; index++)
        {
            Assert.IsTrue(
                ordinals[index] >= ordinals[index - 1],
                $"Etap cofnal sie z {stages[index - 1]} do {stages[index]}.");
        }

        Assert.IsTrue(
            progress.Reports.All(report => !string.IsNullOrWhiteSpace(report.Message)),
            "Kazdy etap musi miec czytelny opis.");
    }

    [TestMethod]
    public async Task NeuralRenderingInstallReportsItsOwnStages()
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore(
            modelFileNames: ["nvngx_dlssnr.dll"]);
        RecordingProgress progress = new();
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        Assert.IsTrue((await manager.InstallAsync(
            NeuralRequest(game, context),
            progress,
            CancellationToken.None)).Succeeded);

        OptiScalerInstallStage[] stages = progress.Reports
            .Select(report => report.Stage)
            .ToArray();
        CollectionAssert.Contains(
            stages,
            OptiScalerInstallStage.CheckingHardware);
        CollectionAssert.Contains(
            stages,
            OptiScalerInstallStage.ConfiguringNeuralRendering);
        CollectionAssert.Contains(
            stages,
            OptiScalerInstallStage.CollectingDriverFiles);
    }

    private sealed class RecordingProgress : IProgress<OptiScalerInstallProgress>
    {
        public List<OptiScalerInstallProgress> Reports { get; } = [];

        public void Report(OptiScalerInstallProgress value) => Reports.Add(value);
    }

    [TestMethod]
    public async Task AgilitySdkUpgradeIsWrittenWithoutNeuralRendering()
    {
        // Czesc gier nie uruchomi OptiScalera bez odswiezonego Agility SDK,
        // a przelacznik jest w pakiecie domyslnie wylaczony. Opcja musi
        // dzialac niezaleznie od Neural Rendering.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadFile(
            Path.Combine("OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll"),
            "agility");
        context.CreatePayloadIni(withBom: true, complete: true);
        bool probed = false;
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () =>
            {
                probed = true;
                return context.CreateDriverStore(modelFileNames: []);
            });

        OptiScalerOperationResult result = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-agility",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true,
                UpgradeAgilitySdk: true),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.IsFalse(probed, "Bez Neural Rendering sterownik nie jest badany.");

        string ini = File.ReadAllText(
            Path.Combine(context.GameDirectory, "OptiScaler.ini"));
        StringAssert.Contains(ini, "FsrAgilitySDKUpgrade=true");

        // Neural Rendering ma pozostac nietkniete.
        StringAssert.Contains(ini, "Dx12Upscaler=auto");

        // Framework musi trafic tam, gdzie pakiet go oczekuje.
        Assert.IsTrue(File.Exists(Path.Combine(
            context.GameDirectory,
            "OptiScaler",
            "D3D12_OptiScaler",
            "D3D12Core.dll")));
    }

    [TestMethod]
    public async Task GameRequiringACompanionIsBlockedUntilItIsPresent()
    {
        // RESIDENT EVIL Requiem wywala sie przy starcie, jesli OptiScaler
        // zostanie zainstalowany bez REFramework. Lepiej odmowic z powodem
        // niz zainstalowac cos, co konczy sie crashem gry.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("re9.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult blocked = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-re9",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(blocked.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.RequiredCompanionMissing,
            blocked.BlockReason);
        StringAssert.Contains(blocked.Message, "REFramework");
        StringAssert.Contains(blocked.Message, "dinput8.dll");
        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "dxgi.dll")));

        // Po dostarczeniu skladnika instalacja przechodzi. REFramework laduje
        // pod wlasna nazwa — dinput8.dll obok gry — bo tak robi to jego
        // instrukcja i tak dziala w praktyce.
        context.CreateGameFile("dinput8.dll", "reframework");

        OptiScalerOperationResult allowed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-re9",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(allowed.Succeeded, allowed.Message);

        // Silnik RE wywala sie przy wlaczonym spoofingu DXGI, a REFramework
        // zajmuje ten sam klawisz overlaya co OptiScaler. Instalacja ma to
        // rozstrzygnac sama, bez odsylania uzytkownika do pliku INI.
        string installedIni = File.ReadAllText(
            Path.Combine(context.GameDirectory, "OptiScaler.ini"));
        StringAssert.Contains(installedIni, "Dxgi=false");
        StringAssert.Contains(installedIni, "ShortcutKey=0x24");
        // OptiScaler nie chainloaduje REFramework: Windows laduje go sam
        // przez dinput8.dll, wiec ten przelacznik zostaje nietkniety.
        StringAssert.Contains(installedIni, "LoadReshade=auto");
    }

    [TestMethod]
    public async Task AnExistingLoaderIsLeftWhereTheUserPutIt()
    {
        // REFramework laduje jako dinput8.dll, wiec plik o tej nazwie obok gry
        // spelnia wymaganie. GameShift ani go nie pobiera drugi raz, ani nie
        // podmienia: nie polozyl go tam i nie wie, co uzytkownik ma w modach.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("re9.exe", "game");
        context.CreateGameFile("dinput8.dll", "reframework-uzytkownika");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: false, complete: true);
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult allowed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-re9",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(allowed.Succeeded, allowed.Message);
        Assert.AreEqual(
            "reframework-uzytkownika",
            File.ReadAllText(
                Path.Combine(context.GameDirectory, "dinput8.dll")).Trim(),
            "Cudzy loader mial zostac nietkniety.");

        OptiScalerOperationResult removed = await manager.RemoveAsync(
            "profile-re9",
            CancellationToken.None);

        Assert.IsTrue(removed.Succeeded, removed.Message);
        Assert.AreEqual(
            "reframework-uzytkownika",
            File.ReadAllText(
                Path.Combine(context.GameDirectory, "dinput8.dll")).Trim(),
            "Deinstalacja tez nie miala go ruszac.");
    }

    [TestMethod]
    public async Task GameIsBlockedWhenProxyIsNotTheOneItLoads()
    {
        // Diablo II: Resurrected laduje OptiScalera tylko jako winmm.dll.
        // Instalacja pod dxgi.dll przeszlaby bez bledu i nie zrobila nic,
        // wiec odmawiamy zamiast zostawiac martwy plik w katalogu gry.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("D2R.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: false, complete: true);
        OptiScalerManager manager = context.CreateManager();

        OptiScalerOperationResult blocked = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-d2r",
                game,
                context.GameDirectory,
                OptiScalerProxy.Dxgi,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsFalse(blocked.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.ProxyNotSupportedByGame,
            blocked.BlockReason);
        StringAssert.Contains(blocked.Message, "winmm.dll");
        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "dxgi.dll")));

        OptiScalerOperationResult allowed = await manager.InstallAsync(
            new OptiScalerInstallRequest(
                "profile-d2r",
                game,
                context.GameDirectory,
                OptiScalerProxy.Winmm,
                OfflineUseConfirmed: true),
            CancellationToken.None);

        Assert.IsTrue(allowed.Succeeded, allowed.Message);
    }

    [TestMethod]
    public void UnknownGameHasNoCompanionRequirement()
    {
        Assert.IsNull(OptiScalerGameRequirements.Find(@"C:\Gry\Inna\game.exe"));
        Assert.IsNotNull(OptiScalerGameRequirements.Find(@"C:\Gry\RE9\re9.exe"));

        // Gra moze wymagac konkretnego proxy, nie wymagajac zadnego dodatku.
        OptiScalerGameRequirement forspoken =
            OptiScalerGameRequirements.Find(@"C:\Gry\Forspoken\Forspoken.exe")!;
        Assert.IsFalse(forspoken.NeedsCompanion);
        Assert.AreEqual(OptiScalerProxy.D3d12, forspoken.RequiredProxy);
        Assert.IsTrue(OptiScalerGameRequirements.IsSatisfied(forspoken, []));
    }

    private static async Task<OptiScalerOperationResult>
        RunBlockedNeuralInstallAsync(
            Func<OptiScalerTestContext, NvidiaDriverStoreSnapshot> driverStore)
    {
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("Game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "optiscaler proxy");
        context.CreatePayloadIni(withBom: true, complete: true);
        NvidiaDriverStoreSnapshot snapshot = driverStore(context);
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => snapshot);

        OptiScalerOperationResult result = await manager.InstallAsync(
            NeuralRequest(game, context),
            CancellationToken.None);

        Assert.IsFalse(File.Exists(
            Path.Combine(context.GameDirectory, "dxgi.dll")));
        return result;
    }

    [TestMethod]
    public async Task BothSuppliedLibrariesLandNextToTheGame()
    {
        // Sterownik 616.x nie niesie ani modelu, ani runtime'u DLSS, wiec oba
        // przychodza od uzytkownika. Zaden nie ma waznego podpisu — tak wyglada
        // kazda krazaca kopia — wiec wchodza dopiero po swiadomej zgodzie.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: false, complete: true);
        string model = context.CreateOutsideFile(
            NvidiaDriverStoreProbe.NeuralRenderingModelFileName,
            "wskazany model");
        string runtime = context.CreateOutsideFile(
            NvidiaDriverStoreProbe.UpscalerModelFileName,
            "wskazany runtime");
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore();
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        OptiScalerOperationResult refused = await manager.InstallAsync(
            NeuralRequest(game, context) with
            {
                NeuralRenderingModelPaths = [model, runtime],
            },
            CancellationToken.None);

        Assert.IsFalse(refused.Succeeded);
        Assert.AreEqual(
            OptiScalerSafetyBlockReason.NeuralRenderingModelMissing,
            refused.BlockReason);

        OptiScalerOperationResult result = await manager.InstallAsync(
            NeuralRequest(game, context) with
            {
                NeuralRenderingModelPaths = [model, runtime],
                UnverifiedNeuralModelAccepted = true,
            },
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            "wskazany model",
            File.ReadAllText(Path.Combine(
                context.GameDirectory,
                NvidiaDriverStoreProbe.NeuralRenderingModelFileName)).Trim());
        Assert.AreEqual(
            "wskazany runtime",
            File.ReadAllText(Path.Combine(
                context.GameDirectory,
                NvidiaDriverStoreProbe.UpscalerModelFileName)).Trim(),
            "Bez nvngx_dlss.dll przelacznik upscalera nie ma czego zaladowac.");
    }

    [TestMethod]
    public async Task TheDriverCopyBeatsAnythingThePlayerPicks()
    {
        // W magazynie sterownikow pisze tylko TrustedInstaller, wiec ta kopia
        // jest pewniejsza niz plik wskazany z dysku — i to ona ma trafic do
        // gry, nawet gdy uzytkownik wskaze wlasna.
        using OptiScalerTestContext context = new();
        string game = context.CreateGameFile("game.exe", "game");
        context.CreatePayloadFile("OptiScaler.dll", "proxy");
        context.CreatePayloadIni(withBom: false, complete: true);
        string runtime = context.CreateOutsideFile(
            NvidiaDriverStoreProbe.UpscalerModelFileName,
            "wskazany runtime");
        NvidiaDriverStoreSnapshot driverStore = context.CreateDriverStore(
            modelFileNames:
            [
                NvidiaDriverStoreProbe.NeuralRenderingModelFileName,
                NvidiaDriverStoreProbe.UpscalerModelFileName,
            ]);
        OptiScalerManager manager = context.CreateManager(
            driverStoreProbe: () => driverStore);

        OptiScalerOperationResult result = await manager.InstallAsync(
            NeuralRequest(game, context) with
            {
                NeuralRenderingModelPaths = [runtime],
            },
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Message);
        Assert.AreEqual(
            "driver " + NvidiaDriverStoreProbe.UpscalerModelFileName,
            File.ReadAllText(Path.Combine(
                context.GameDirectory,
                NvidiaDriverStoreProbe.UpscalerModelFileName)).Trim());
    }

    private static OptiScalerInstallRequest NeuralRequest(
        string game,
        OptiScalerTestContext context) =>
        new(
            "profile-nr",
            game,
            context.GameDirectory,
            OptiScalerProxy.Dxgi,
            OfflineUseConfirmed: true,
            Channel: OptiScalerReleaseChannel.DlssNeuralRendering,
            Version: null,
            ExperimentalUseConfirmed: true,
            EnableNeuralRendering: true);

    private sealed class OptiScalerTestContext : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "GameShift-OptiScaler-" + Guid.NewGuid().ToString("N"));

        public OptiScalerTestContext()
        {
            Directory.CreateDirectory(GameDirectory);
            Directory.CreateDirectory(PayloadDirectory);
        }

        public string GameDirectory => Path.Combine(_root, "Game");

        public string PayloadDirectory => Path.Combine(_root, "Payload");

        public string OutsideDirectory => Path.Combine(_root, "Outside");

        public FakePackageSource PackageSource { get; } = new();

        public string CreateGameFile(string relativePath, string content) =>
            CreateFile(GameDirectory, relativePath, content);

        public string CreatePayloadFile(string relativePath, string content) =>
            CreateFile(PayloadDirectory, relativePath, content);

        /// <summary>
        /// A file the player picked by hand, so it has to sit outside both the
        /// game directory and the package.
        /// </summary>
        public string CreateOutsideFile(string relativePath, string content) =>
            CreateFile(OutsideDirectory, relativePath, content);

        public OptiScalerManager CreateManager(
            IOptiScalerPackageSource? packageSource = null,
            Func<NvidiaDriverStoreSnapshot>? driverStoreProbe = null)
        {
            PackageSource.PayloadDirectory = PayloadDirectory;
            return new(
                Path.Combine(_root, "State"),
                packageSource ?? PackageSource,
                new CopyingArchiveExtractor(PayloadDirectory),
                _ => false,
                driverStoreProbe);
        }

        public string DriverStoreDirectory => Path.Combine(_root, "DriverStore");

        /// <summary>
        /// Builds a driver store snapshot without touching the real machine,
        /// so the DLSS paths stay testable on non-NVIDIA hardware.
        /// </summary>
        public NvidiaDriverStoreSnapshot CreateDriverStore(
            bool isGeForce = true,
            GeForceGeneration generation = GeForceGeneration.Rtx50,
            NvidiaDriverVersion? driver = null,
            params string[] modelFileNames)
        {
            Directory.CreateDirectory(DriverStoreDirectory);
            List<NvidiaModelFile> files = [];
            foreach (string fileName in modelFileNames)
            {
                string path = Path.Combine(DriverStoreDirectory, fileName);
                File.WriteAllText(path, "driver " + fileName);
                files.Add(new(fileName, path));
            }

            return new(
                new(
                    isGeForce,
                    generation,
                    driver ?? new NvidiaDriverVersion(616, 56),
                    files.Any(file => string.Equals(
                        file.FileName,
                        NvidiaDriverStoreProbe.NeuralRenderingModelFileName,
                        StringComparison.OrdinalIgnoreCase))),
                DriverStoreDirectory,
                files);
        }

        public void CreatePayloadIni(bool withBom, bool complete)
        {
            string[] lines = complete
                ?
                [
                    "[Upscalers]",
                    "Dx12Upscaler=auto",
                    "Dx11Upscaler=auto",
                    "VulkanUpscaler=auto",
                    "[DLSS]",
                    "Enabled=auto",
                    "[DlssNr]",
                    "Enabled=auto",
                    "[FSR]",
                    "FsrAgilitySDKUpgrade=auto",
                    "[Menu]",
                    "ShortcutKey=auto",
                    "[Spoofing]",
                    "Dxgi=auto",
                    "[Plugins]",
                    "LoadReshade=auto",
                ]
                : ["[Upscalers]", "Dx12Upscaler=auto"];
            string content = string.Join("\r\n", lines) + "\r\n";
            byte[] body = new UTF8Encoding(false).GetBytes(content);
            byte[] bytes = withBom
                ? [0xEF, 0xBB, 0xBF, .. body]
                : body;
            File.WriteAllBytes(
                Path.Combine(PayloadDirectory, "OptiScaler.ini"),
                bytes);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static string CreateFile(
            string root,
            string relativePath,
            string content)
        {
            string path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }

    private sealed class TimeoutPackageSource : IOptiScalerPackageSource
    {
        public ValueTask<OptiScalerPackageDescriptor> GetLatestAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromException<OptiScalerPackageDescriptor>(
                new TaskCanceledException("HTTP request timed out."));

        public ValueTask<string> DownloadAsync(
            OptiScalerPackageDescriptor package,
            string cacheDirectory,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Download should not start.");
    }

    private sealed class FakePackageSource : IOptiScalerPackageSource
    {
        public int AvailableRequestCount { get; private set; }

        public int DownloadCount { get; private set; }

        public string? DownloadedVersion { get; private set; }

        public OptiScalerReleaseChannel? RequestedChannel { get; private set; }

        public string PayloadDirectory { get; set; } = string.Empty;

        public ValueTask<IReadOnlyList<OptiScalerPackageDescriptor>> GetAvailableAsync(
            OptiScalerReleaseChannel channel,
            CancellationToken cancellationToken)
        {
            AvailableRequestCount++;
            RequestedChannel = channel;
            IReadOnlyList<OptiScalerPackageDescriptor> packages = channel switch
            {
                OptiScalerReleaseChannel.Stable =>
                [
                    CreatePackage("0.9.4", channel),
                ],
                OptiScalerReleaseChannel.Beta =>
                [
                    CreatePackage("0.9.5-pre3", channel),
                    CreatePackage("0.9.5-pre2", channel),
                ],
                OptiScalerReleaseChannel.Nightly =>
                [
                    CreatePackage("nightly-20260904", channel),
                ],
                OptiScalerReleaseChannel.DlssNeuralRendering =>
                [
                    CreatePackage("0.2.0-dlssnr", channel),
                ],
                _ => [],
            };
            return ValueTask.FromResult(packages);
        }

        public ValueTask<OptiScalerPackageDescriptor> GetLatestAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CreatePackage(
                "0.9.4",
                OptiScalerReleaseChannel.Stable));

        public ValueTask<string> DownloadAsync(
            OptiScalerPackageDescriptor package,
            string cacheDirectory,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            DownloadedVersion = package.Version;
            Directory.CreateDirectory(cacheDirectory);
            string archive = Path.Combine(cacheDirectory, "package.7z");
            File.WriteAllText(archive, PayloadDirectory);
            return ValueTask.FromResult(archive);
        }

        private static OptiScalerPackageDescriptor CreatePackage(
            string version,
            OptiScalerReleaseChannel channel) =>
            new(
                version,
                new Uri(
                    "https://github.com/optiscaler/OptiScaler/releases/download/"
                    + version
                    + "/package.7z"),
                7,
                new string('a', 64),
                channel);
    }

    private sealed class CopyingArchiveExtractor(string payloadDirectory)
        : IOptiScalerArchiveExtractor
    {
        public ValueTask ExtractAsync(
            string archivePath,
            string destinationDirectory,
            CancellationToken cancellationToken)
        {
            foreach (string source in Directory.EnumerateFiles(
                         payloadDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(payloadDirectory, source);
                string destination = Path.Combine(destinationDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }

            return ValueTask.CompletedTask;
        }
    }
}

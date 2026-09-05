using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameShift.Core.OptiScaler;

namespace GameShift.Windows.OptiScaler;

public sealed record OptiScalerInstallRequest(
    string ProfileId,
    string GameExecutablePath,
    string? WorkingDirectory,
    OptiScalerProxy Proxy,
    bool OfflineUseConfirmed,
    OptiScalerReleaseChannel Channel = OptiScalerReleaseChannel.Stable,
    string? Version = null,
    bool ExperimentalUseConfirmed = false,
    bool EnableNeuralRendering = false);

public sealed record OptiScalerOperationResult(
    bool Succeeded,
    string Message,
    string? TargetExecutablePath = null,
    OptiScalerSafetyBlockReason BlockReason = OptiScalerSafetyBlockReason.None,
    IReadOnlyList<string>? Conflicts = null)
{
    public IReadOnlyList<string> ConflictingPaths => Conflicts ?? [];
}

public sealed record OptiScalerInstallationStatus(
    bool IsInstalled,
    string? Version = null,
    string? TargetExecutablePath = null,
    OptiScalerProxy Proxy = OptiScalerProxy.Dxgi,
    OptiScalerReleaseChannel Channel = OptiScalerReleaseChannel.Stable);

public sealed record OptiScalerReleaseOption(
    OptiScalerReleaseChannel Channel,
    string Version,
    DateTimeOffset? PublishedAtUtc)
{
    public bool IsExperimental =>
        OptiScalerReleaseChannelPolicy.IsExperimental(Channel);
}

public enum OptiScalerInstallStage
{
    Preparing = 1,
    CheckingHardware = 2,
    Downloading = 3,
    Extracting = 4,
    CollectingDriverFiles = 5,
    ConfiguringNeuralRendering = 6,
    BackingUp = 7,
    CopyingFiles = 8,
    Completed = 9,
}

public sealed record OptiScalerDownloadProgress(
    long DownloadedBytes,
    long TotalBytes)
{
    public double Percent => TotalBytes <= 0
        ? 0
        : Math.Clamp(100.0 * DownloadedBytes / TotalBytes, 0, 100);
}

public sealed record OptiScalerInstallProgress(
    OptiScalerInstallStage Stage,
    string Message,
    double? Percent = null);

public sealed record OptiScalerInstallPreflight(
    string TargetExecutablePath,
    OptiScalerSafetyDecision Safety);

internal sealed record OptiScalerPackageDescriptor(
    string Version,
    Uri DownloadUri,
    long Size,
    string Sha256,
    OptiScalerReleaseChannel Channel = OptiScalerReleaseChannel.Stable,
    DateTimeOffset? PublishedAtUtc = null);

internal interface IOptiScalerPackageSource
{
    async ValueTask<IReadOnlyList<OptiScalerPackageDescriptor>> GetAvailableAsync(
        OptiScalerReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        if (channel != OptiScalerReleaseChannel.Stable)
        {
            throw new InvalidDataException("Źródło nie obsługuje tego kanału OptiScaler.");
        }

        return [await GetLatestAsync(cancellationToken).ConfigureAwait(false)];
    }

    ValueTask<OptiScalerPackageDescriptor> GetLatestAsync(
        CancellationToken cancellationToken);

    ValueTask<string> DownloadAsync(
        OptiScalerPackageDescriptor package,
        string cacheDirectory,
        CancellationToken cancellationToken);

    async ValueTask<string> DownloadAsync(
        OptiScalerPackageDescriptor package,
        string cacheDirectory,
        IProgress<OptiScalerDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        await DownloadAsync(package, cacheDirectory, cancellationToken)
            .ConfigureAwait(false);
}

internal interface IOptiScalerArchiveExtractor
{
    ValueTask ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

public sealed class OptiScalerManager : IDisposable
{
    private const int MaximumScannedFiles = 4096;
    private const int MaximumScanDepth = 6;
    private static readonly TimeSpan ReleaseCacheLifetime =
        TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _stateDirectory;
    private readonly IOptiScalerPackageSource _packageSource;
    private readonly IOptiScalerArchiveExtractor _archiveExtractor;
    private readonly Func<string, bool> _isExecutableRunning;
    private readonly Func<NvidiaDriverStoreSnapshot> _driverStoreProbe;
    private readonly ConcurrentDictionary<
        OptiScalerReleaseChannel,
        CachedOptiScalerPackages> _releaseCache = new();
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public OptiScalerManager(string? stateDirectory = null)
        : this(
            stateDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameShift",
                "OptiScaler"),
            new GitHubOptiScalerPackageSource(),
            new SharpCompressOptiScalerArchiveExtractor(),
            IsExecutableRunning,
            driverStoreProbe: null)
    {
    }

    internal OptiScalerManager(
        string stateDirectory,
        IOptiScalerPackageSource packageSource,
        IOptiScalerArchiveExtractor archiveExtractor,
        Func<string, bool> isExecutableRunning,
        Func<NvidiaDriverStoreSnapshot>? driverStoreProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(packageSource);
        ArgumentNullException.ThrowIfNull(archiveExtractor);
        ArgumentNullException.ThrowIfNull(isExecutableRunning);
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _packageSource = packageSource;
        _archiveExtractor = archiveExtractor;
        _isExecutableRunning = isExecutableRunning;
        _driverStoreProbe = driverStoreProbe
            ?? (static () => new NvidiaDriverStoreProbe().Probe());
    }

    public OptiScalerInstallationStatus GetStatus(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        OptiScalerInstallationManifest? manifest = ReadManifest(profileId);
        return manifest is null
            ? new(false)
            : new(
                true,
                manifest.PackageVersion,
                manifest.TargetExecutablePath,
                manifest.Proxy,
                manifest.Channel);
    }

    public async ValueTask<IReadOnlyList<OptiScalerReleaseOption>>
        GetAvailableVersionsAsync(
            OptiScalerReleaseChannel channel,
            CancellationToken cancellationToken)
    {
        IReadOnlyList<OptiScalerPackageDescriptor> releases =
            await GetAvailablePackagesAsync(channel, cancellationToken)
                .ConfigureAwait(false);
        return Array.AsReadOnly(
            releases
            .Select(release => new OptiScalerReleaseOption(
                release.Channel,
                release.Version,
                release.PublishedAtUtc))
            .ToArray());
    }

    public void Dispose() => _operationLock.Dispose();

    public OptiScalerInstallPreflight EvaluateInstall(
        OptiScalerInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        string targetExecutable = ResolveTargetExecutable(
            request.GameExecutablePath,
            request.WorkingDirectory);
        string scanRoot = ResolveGameRoot(
            Path.GetDirectoryName(Path.GetFullPath(request.GameExecutablePath))!,
            request.WorkingDirectory);
        string[] gameFiles = EnumerateFilesBounded(scanRoot)
            .Select(path => Path.GetRelativePath(scanRoot, path))
            .ToArray();
        OptiScalerSafetyDecision decision = OptiScalerSafetyPolicy.Evaluate(
            _isExecutableRunning(targetExecutable),
            request.OfflineUseConfirmed,
            gameFiles);
        return new(targetExecutable, decision);
    }

    public ValueTask<OptiScalerOperationResult> InstallAsync(
        OptiScalerInstallRequest request,
        CancellationToken cancellationToken) =>
        InstallAsync(request, null, cancellationToken);

    public async ValueTask<OptiScalerOperationResult> InstallAsync(
        OptiScalerInstallRequest request,
        IProgress<OptiScalerInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask<OptiScalerOperationResult> RemoveAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OptiScalerInstallationManifest? manifest = ReadManifest(profileId);
            if (manifest is null)
            {
                return new(true, "OptiScaler nie jest zainstalowany dla tej gry.");
            }

            if (_isExecutableRunning(manifest.TargetExecutablePath))
            {
                return new(
                    false,
                    "Najpierw zamknij grę.",
                    manifest.TargetExecutablePath,
                    OptiScalerSafetyBlockReason.GameRunning);
            }

            List<string> conflicts = FindManagedFileConflicts(manifest);
            if (conflicts.Count > 0)
            {
                return new(
                    false,
                    "Pliki OptiScaler zostały zmienione poza GameShift. Odinstalowanie przerwano, aby nie utracić danych.",
                    manifest.TargetExecutablePath,
                    Conflicts: conflicts);
            }

            string transactionDirectory = CreateTemporaryDirectory("remove");
            try
            {
                Dictionary<string, string?> rollback = SnapshotDestinations(
                    manifest.TargetDirectory,
                    manifest.Files.Select(file => file.RelativePath),
                    transactionDirectory,
                    cancellationToken);
                try
                {
                    foreach (OptiScalerInstalledFile file in manifest.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string destination = GetContainedPath(
                            manifest.TargetDirectory,
                            file.RelativePath);
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }

                        if (file.OriginalBackupRelativePath is not null)
                        {
                            string backup = GetContainedPath(
                                _stateDirectory,
                                file.OriginalBackupRelativePath);
                            if (!File.Exists(backup))
                            {
                                throw new InvalidDataException(
                                    $"Brakuje kopii oryginalnego pliku: {file.RelativePath}");
                            }

                            AtomicCopy(backup, destination);
                        }
                    }

                    DeleteEmptyDirectories(manifest.TargetDirectory, manifest.Files);
                    File.Delete(GetManifestPath(profileId));
                    DeleteBackupDirectory(profileId);
                    return new(
                        true,
                        "OptiScaler został usunięty, a wcześniejsze pliki przywrócono.",
                        manifest.TargetExecutablePath);
                }
                catch
                {
                    RestoreDestinations(manifest.TargetDirectory, rollback);
                    throw;
                }
            }
            finally
            {
                TryDeleteDirectory(transactionDirectory);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new(false, $"Nie udało się usunąć OptiScaler: {exception.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public static string ResolveTargetExecutable(
        string gameExecutablePath,
        string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutablePath);
        string executable = Path.GetFullPath(gameExecutablePath);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Nie znaleziono pliku EXE gry.", executable);
        }

        string executableDirectory = Path.GetDirectoryName(executable)!;
        string root = ResolveGameRoot(executableDirectory, workingDirectory);
        List<string> shippingExecutables = EnumerateFilesBounded(root)
            .Where(path =>
                Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && (Path.GetFileNameWithoutExtension(path)
                        .EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileNameWithoutExtension(path)
                        .EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase)))
            .Where(path => !Path.GetRelativePath(root, path)
                .StartsWith("Engine" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path).Count(
                character => character == Path.DirectorySeparatorChar))
            .ThenBy(path => path.Length)
            .ToList();

        return shippingExecutables.Count > 0
            ? shippingExecutables[0]
            : executable;
    }

    private async ValueTask<OptiScalerOperationResult> InstallCoreAsync(
        OptiScalerInstallRequest request,
        IProgress<OptiScalerInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProfileId);
        progress?.Report(new(
            OptiScalerInstallStage.Preparing,
            "Sprawdzanie gry i zasad bezpieczeństwa…"));
        OptiScalerInstallPreflight preflight;
        try
        {
            preflight = EvaluateInstall(request);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            return new(false, exception.Message);
        }
        string targetExecutable = preflight.TargetExecutablePath;
        OptiScalerSafetyDecision decision = preflight.Safety;
        if (!decision.CanInstall)
        {
            return new(
                false,
                GetSafetyMessage(decision),
                targetExecutable,
                decision.BlockReason,
                decision.Evidence is null ? null : [decision.Evidence]);
        }

        if (OptiScalerReleaseChannelPolicy.IsExperimental(request.Channel)
            && !request.ExperimentalUseConfirmed)
        {
            return new(
                false,
                "Potwierdź ryzyko wersji eksperymentalnej OptiScaler.",
                targetExecutable,
                OptiScalerSafetyBlockReason.ExperimentalUseNotConfirmed);
        }

        NvidiaDriverStoreSnapshot? driverStore = null;
        if (request.EnableNeuralRendering)
        {
            progress?.Report(new(
                OptiScalerInstallStage.CheckingHardware,
                "Sprawdzanie karty graficznej i sterownika NVIDIA…"));
            driverStore = _driverStoreProbe();
            OptiScalerSafetyDecision neural = NeuralRenderingPolicy.Evaluate(
                decision,
                driverStore.Capability);
            if (!neural.CanInstall)
            {
                return new(
                    false,
                    GetSafetyMessage(neural),
                    targetExecutable,
                    neural.BlockReason,
                    neural.Evidence is null ? null : [neural.Evidence]);
            }
        }

        string extractionDirectory = CreateTemporaryDirectory("extract");
        try
        {
            IReadOnlyList<OptiScalerPackageDescriptor> releases =
                await GetAvailablePackagesAsync(
                        request.Channel,
                        cancellationToken)
                    .ConfigureAwait(false);
            OptiScalerPackageDescriptor? package = string.IsNullOrWhiteSpace(
                request.Version)
                ? releases.Count > 0 ? releases[0] : null
                : releases.FirstOrDefault(release => release.Version.Equals(
                    request.Version,
                    StringComparison.OrdinalIgnoreCase));
            if (package is null)
            {
                return new(
                    false,
                    "Wybrana wersja nie jest już dostępna w tym kanale.",
                    targetExecutable);
            }

            progress?.Report(new(
                OptiScalerInstallStage.Downloading,
                $"Pobieranie pakietu OptiScaler {package.Version}…",
                0));
            Progress<OptiScalerDownloadProgress>? downloadProgress =
                progress is null
                    ? null
                    : new(update => progress.Report(new(
                        OptiScalerInstallStage.Downloading,
                        "Pobieranie i weryfikowanie pakietu: "
                            + $"{update.DownloadedBytes / (1024.0 * 1024.0):0.0}"
                            + $" / {update.TotalBytes / (1024.0 * 1024.0):0.0} MiB",
                        update.Percent)));
            string archivePath = await _packageSource.DownloadAsync(
                    package,
                    Path.Combine(_stateDirectory, "Packages"),
                    downloadProgress,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new(
                OptiScalerInstallStage.Extracting,
                "Rozpakowywanie zweryfikowanego pakietu…"));
            await _archiveExtractor.ExtractAsync(
                    archivePath,
                    extractionDirectory,
                    cancellationToken)
                .ConfigureAwait(false);

            string mainLibrary = Path.Combine(extractionDirectory, "OptiScaler.dll");
            if (!File.Exists(mainLibrary))
            {
                return new(false, "Oficjalny pakiet nie zawiera pliku OptiScaler.dll.");
            }

            List<PayloadFile> additionalFiles = [];
            if (driverStore is not null)
            {
                progress?.Report(new(
                    OptiScalerInstallStage.ConfiguringNeuralRendering,
                    "Włączanie Neural Rendering w konfiguracji…"));
                OptiScalerIniPatchResult? iniPatch =
                    PatchNeuralRenderingIni(extractionDirectory);
                if (iniPatch is null)
                {
                    return new(
                        false,
                        "Pakiet nie zawiera OptiScaler.ini, więc nie da się "
                            + "włączyć Neural Rendering.",
                        targetExecutable);
                }

                if (iniPatch.NotFound.Count > 0)
                {
                    string missing = string.Join(
                        ", ",
                        iniPatch.NotFound.Select(
                            setting => $"[{setting.Section}] {setting.Key}"));
                    return new(
                        false,
                        "Ten pakiet nie obsługuje Neural Rendering; brakuje "
                            + $"ustawień: {missing}.",
                        targetExecutable);
                }

                progress?.Report(new(
                    OptiScalerInstallStage.CollectingDriverFiles,
                    $"Kopiowanie {driverStore.ModelFiles.Count} plików DLSS "
                        + "ze sterownika NVIDIA…"));
                additionalFiles.AddRange(driverStore.ModelFiles.Select(
                    file => new PayloadFile(file.FullPath, file.FileName)));
            }

            List<PayloadFile> payload = BuildPayload(
                extractionDirectory,
                request.Proxy,
                additionalFiles);
            return Deploy(
                request,
                targetExecutable,
                package.Version,
                payload,
                progress,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return new(
                false,
                "Przekroczono czas pobierania OptiScaler. Spróbuj ponownie później.",
                targetExecutable);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException)
        {
            return new(
                false,
                $"Nie udało się zainstalować OptiScaler: {exception.Message}",
                targetExecutable);
        }
        finally
        {
            TryDeleteDirectory(extractionDirectory);
        }
    }

    private async ValueTask<IReadOnlyList<OptiScalerPackageDescriptor>>
        GetAvailablePackagesAsync(
            OptiScalerReleaseChannel channel,
            CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_releaseCache.TryGetValue(channel, out CachedOptiScalerPackages? cached)
            && cached.ExpiresAtUtc > now)
        {
            return cached.Packages;
        }

        IReadOnlyList<OptiScalerPackageDescriptor> packages = Array.AsReadOnly(
            (await _packageSource.GetAvailableAsync(channel, cancellationToken)
                .ConfigureAwait(false)).ToArray());
        _releaseCache[channel] = new(
            packages,
            now.Add(ReleaseCacheLifetime));
        return packages;
    }

    private OptiScalerOperationResult Deploy(
        OptiScalerInstallRequest request,
        string targetExecutable,
        string packageVersion,
        IReadOnlyList<PayloadFile> payload,
        IProgress<OptiScalerInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string targetDirectory = Path.GetDirectoryName(targetExecutable)!;
        OptiScalerInstallationManifest? previous = ReadManifest(request.ProfileId);
        if (previous is not null
            && !Path.GetFullPath(previous.TargetDirectory).Equals(
                Path.GetFullPath(targetDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                false,
                "Gra zmieniła katalog. Najpierw usuń poprzednią instalację OptiScaler.",
                targetExecutable);
        }

        if (previous is not null)
        {
            List<string> conflicts = FindManagedFileConflicts(previous);
            if (conflicts.Count > 0)
            {
                return new(
                    false,
                    "Pliki poprzedniej instalacji zostały zmienione poza GameShift.",
                    targetExecutable,
                    Conflicts: conflicts);
            }
        }

        string transactionDirectory = CreateTemporaryDirectory("deploy");
        string manifestPath = GetManifestPath(request.ProfileId);
        byte[]? previousManifest = File.Exists(manifestPath)
            ? File.ReadAllBytes(manifestPath)
            : null;
        string backupRoot = GetBackupDirectory(request.ProfileId);
        HashSet<string> newlyCreatedBackups = new(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> affected = payload.Select(file => file.RelativePath)
            .Concat(previous?.Files.Select(file => file.RelativePath) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new(
            OptiScalerInstallStage.BackingUp,
            "Tworzenie kopii zapasowej plików gry…"));
        Dictionary<string, string?> rollback = SnapshotDestinations(
            targetDirectory,
            affected,
            transactionDirectory,
            cancellationToken);
        progress?.Report(new(
            OptiScalerInstallStage.CopyingFiles,
            $"Instalowanie {payload.Count} plików w katalogu gry…"));

        try
        {
            Dictionary<string, OptiScalerInstalledFile> previousFiles =
                previous?.Files.ToDictionary(
                    file => file.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase);
            List<OptiScalerInstalledFile> installedFiles = [];

            foreach (PayloadFile file in payload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = GetContainedPath(targetDirectory, file.RelativePath);
                string? originalBackup = previousFiles.TryGetValue(
                    file.RelativePath,
                    out OptiScalerInstalledFile? previousFile)
                    ? previousFile.OriginalBackupRelativePath
                    : null;
                if (previousFile is null && File.Exists(destination))
                {
                    string backup = GetContainedPath(backupRoot, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    newlyCreatedBackups.Add(backup);
                    originalBackup = Path.GetRelativePath(_stateDirectory, backup);
                }

                AtomicCopy(file.SourcePath, destination);
                installedFiles.Add(new(
                    file.RelativePath,
                    ComputeSha256(destination),
                    originalBackup));
            }

            HashSet<string> desired = payload.Select(file => file.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (previous is not null)
            {
                foreach (OptiScalerInstalledFile obsolete in previous.Files
                             .Where(file => !desired.Contains(file.RelativePath)))
                {
                    string destination = GetContainedPath(targetDirectory, obsolete.RelativePath);
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }

                    if (obsolete.OriginalBackupRelativePath is not null)
                    {
                        AtomicCopy(
                            GetContainedPath(
                                _stateDirectory,
                                obsolete.OriginalBackupRelativePath),
                            destination);
                    }
                }
            }

            OptiScalerInstallationManifest manifest = new(
                1,
                request.ProfileId,
                packageVersion,
                targetExecutable,
                targetDirectory,
                request.Proxy,
                DateTimeOffset.UtcNow,
                installedFiles,
                request.Channel);
            WriteManifestAtomic(manifestPath, manifest);
            progress?.Report(new(
                OptiScalerInstallStage.Completed,
                $"Gotowe. Zainstalowano {payload.Count} plików.",
                100));
            return new(
                true,
                previous is null
                    ? $"OptiScaler {packageVersion} został zainstalowany."
                    : $"OptiScaler został zaktualizowany do {packageVersion}.",
                targetExecutable);
        }
        catch
        {
            RestoreDestinations(targetDirectory, rollback);
            foreach (string backup in newlyCreatedBackups)
            {
                TryDeleteFile(backup);
            }

            if (previousManifest is null)
            {
                TryDeleteFile(manifestPath);
            }
            else
            {
                WriteBytesAtomic(manifestPath, previousManifest);
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(transactionDirectory);
        }
    }

    private static readonly HashSet<string> NonRuntimeExtensions = new(
        [".bat", ".cmd", ".sh", ".ps1"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides what from an extracted package belongs in the game directory.
    /// Packages differ in layout — the official build keeps D3D12_Optiscaler
    /// at the root while the Neural Rendering fork nests every upscaler
    /// backend under OptiScaler\ — so an allow-list of directory names
    /// silently dropped ~180 MB of runtime. Everything ships except setup
    /// scripts, which GameShift replaces, and the extraction marker file.
    /// Licences stay: the payload is GPL and must carry them.
    /// </summary>
    private static bool IsInstallablePayloadEntry(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        if (extension.Length == 0)
        {
            // The fork ships a zero-byte "!! EXTRACT ALL FILES ... !!" marker.
            return false;
        }

        return !NonRuntimeExtensions.Contains(extension);
    }

    private static List<PayloadFile> BuildPayload(
        string extractionDirectory,
        OptiScalerProxy proxy,
        IReadOnlyList<PayloadFile>? additionalFiles = null)
    {
        string root = Path.GetFullPath(extractionDirectory);
        List<PayloadFile> payload = [];
        foreach (string source in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, source);
            if (!IsInstallablePayloadEntry(relative))
            {
                continue;
            }

            string destinationRelative = relative.Equals(
                "OptiScaler.dll",
                StringComparison.OrdinalIgnoreCase)
                ? OptiScalerSafetyPolicy.GetProxyFileName(proxy)
                : relative;
            payload.Add(new(source, destinationRelative));
        }

        foreach (PayloadFile additional in additionalFiles ?? [])
        {
            // Driver files win over anything the archive shipped under the
            // same name, so the model always matches the installed driver.
            payload.RemoveAll(file => file.RelativePath.Equals(
                additional.RelativePath,
                StringComparison.OrdinalIgnoreCase));
            payload.Add(additional);
        }

        if (payload.Count == 0 || payload.Count > 512)
        {
            throw new InvalidDataException("Pakiet OptiScaler ma nieprawidłową zawartość.");
        }

        return payload;
    }

    /// <summary>
    /// Rewrites the extracted OptiScaler.ini so the package installs with
    /// Neural Rendering enabled. Returns null when the package has no INI.
    /// The file is UTF-8 with a BOM and CRLF endings, both of which survive.
    /// </summary>
    private static OptiScalerIniPatchResult? PatchNeuralRenderingIni(
        string extractionDirectory)
    {
        string iniPath = Path.Combine(extractionDirectory, "OptiScaler.ini");
        if (!File.Exists(iniPath))
        {
            return null;
        }

        byte[] raw = File.ReadAllBytes(iniPath);
        bool hasBom = raw.Length >= 3
            && raw[0] == 0xEF
            && raw[1] == 0xBB
            && raw[2] == 0xBF;
        string content = new UTF8Encoding(false).GetString(
            raw,
            hasBom ? 3 : 0,
            raw.Length - (hasBom ? 3 : 0));

        OptiScalerIniPatchResult result = OptiScalerIniPatcher.Apply(
            content,
            OptiScalerIniPatcher.NeuralRenderingSettings);
        if (result.NotFound.Count == 0)
        {
            File.WriteAllBytes(
                iniPath,
                [
                    .. hasBom ? new byte[] { 0xEF, 0xBB, 0xBF } : [],
                    .. new UTF8Encoding(false).GetBytes(result.Content),
                ]);
        }

        return result;
    }

    private static List<string> FindManagedFileConflicts(
        OptiScalerInstallationManifest manifest)
    {
        List<string> conflicts = [];
        foreach (OptiScalerInstalledFile file in manifest.Files)
        {
            string destination;
            try
            {
                destination = GetContainedPath(manifest.TargetDirectory, file.RelativePath);
            }
            catch (InvalidDataException)
            {
                conflicts.Add(file.RelativePath);
                continue;
            }

            if (File.Exists(destination)
                && !ComputeSha256(destination).Equals(
                    file.InstalledSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(destination);
            }
        }

        return conflicts;
    }

    private static Dictionary<string, string?> SnapshotDestinations(
        string targetDirectory,
        IEnumerable<string> relativePaths,
        string transactionDirectory,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string?> snapshots = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = GetContainedPath(targetDirectory, relativePath);
            if (!File.Exists(destination))
            {
                snapshots[relativePath] = null;
                continue;
            }

            string snapshot = GetContainedPath(transactionDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
            File.Copy(destination, snapshot, overwrite: true);
            snapshots[relativePath] = snapshot;
        }

        return snapshots;
    }

    private static void RestoreDestinations(
        string targetDirectory,
        IReadOnlyDictionary<string, string?> snapshots)
    {
        foreach ((string relativePath, string? snapshot) in snapshots)
        {
            string destination = GetContainedPath(targetDirectory, relativePath);
            if (snapshot is null)
            {
                TryDeleteFile(destination);
            }
            else
            {
                AtomicCopy(snapshot, destination);
            }
        }
    }

    private OptiScalerInstallationManifest? ReadManifest(string profileId)
    {
        string path = GetManifestPath(profileId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            OptiScalerInstallationManifest manifest =
                JsonSerializer.Deserialize<OptiScalerInstallationManifest>(
                File.ReadAllText(path),
                SerializerOptions)
                ?? throw new InvalidDataException(
                    "Plik stanu instalacji OptiScaler jest pusty.");
            ValidateManifest(profileId, manifest);
            return manifest;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            throw new InvalidDataException(
                "Stan instalacji OptiScaler jest uszkodzony.",
                exception);
        }
    }

    private void ValidateManifest(
        string expectedProfileId,
        OptiScalerInstallationManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || !manifest.ProfileId.Equals(
                expectedProfileId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.PackageVersion)
            || string.IsNullOrWhiteSpace(manifest.TargetDirectory)
            || string.IsNullOrWhiteSpace(manifest.TargetExecutablePath)
            || !Enum.IsDefined(manifest.Proxy)
            || manifest.Files is null
            || manifest.Files.Count is 0 or > 512)
        {
            throw new InvalidDataException(
                "Plik stanu instalacji OptiScaler ma nieprawidłowy format.");
        }

        string targetDirectory = Path.GetFullPath(manifest.TargetDirectory);
        string targetExecutable = Path.GetFullPath(manifest.TargetExecutablePath);
        if (!Path.GetExtension(targetExecutable).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(targetExecutable)!.Equals(
                targetDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Cel zapisany w stanie OptiScaler jest nieprawidłowy.");
        }

        HashSet<string> relativePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (OptiScalerInstalledFile file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath)
                || file.InstalledSha256.Length != 64
                || !file.InstalledSha256.All(Uri.IsHexDigit)
                || !relativePaths.Add(file.RelativePath))
            {
                throw new InvalidDataException(
                    "Lista plików instalacji OptiScaler jest nieprawidłowa.");
            }

            _ = GetContainedPath(targetDirectory, file.RelativePath);
            if (file.OriginalBackupRelativePath is not null)
            {
                _ = GetContainedPath(
                    _stateDirectory,
                    file.OriginalBackupRelativePath);
            }
        }
    }

    private static void WriteManifestAtomic(
        string manifestPath,
        OptiScalerInstallationManifest manifest)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        WriteBytesAtomic(manifestPath, bytes);
    }

    private static void WriteBytesAtomic(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private string CreateTemporaryDirectory(string purpose)
    {
        string directory = Path.Combine(
            _stateDirectory,
            "Temporary",
            purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string GetManifestPath(string profileId) => Path.Combine(
        _stateDirectory,
        "Installations",
        GetProfileKey(profileId) + ".json");

    private string GetBackupDirectory(string profileId) => Path.Combine(
        _stateDirectory,
        "Backups",
        GetProfileKey(profileId));

    private void DeleteBackupDirectory(string profileId) =>
        TryDeleteDirectory(GetBackupDirectory(profileId));

    private static string GetProfileKey(string profileId) => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(profileId)))
        .ToLowerInvariant();

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolveGameRoot(
        string executableDirectory,
        string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory)
            && Path.IsPathFullyQualified(workingDirectory)
            && Directory.Exists(workingDirectory))
        {
            return Path.GetFullPath(workingDirectory);
        }

        return Path.GetFullPath(executableDirectory);
    }

    private static List<string> EnumerateFilesBounded(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        List<string> files = [];
        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((fullRoot, 0));
        while (pending.Count > 0 && files.Count < MaximumScannedFiles)
        {
            (string directory, int depth) = pending.Dequeue();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    files.Add(file);
                    if (files.Count >= MaximumScannedFiles)
                    {
                        break;
                    }
                }

                if (depth >= MaximumScanDepth)
                {
                    continue;
                }

                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        return files;
    }

    private static string GetContainedPath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Ścieżka pakietu nie może być bezwzględna.");
        }

        string rootPath = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = rootPath + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Ścieżka pakietu wychodzi poza katalog docelowy.");
        }

        EnsureNoReparsePoint(rootPath, candidate);

        return candidate;
    }

    private static void EnsureNoReparsePoint(string rootPath, string candidate)
    {
        string current = rootPath;
        string relative = Path.GetRelativePath(rootPath, candidate);
        foreach (string component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Ścieżka docelowa zawiera dowiązanie lub punkt ponownej analizy.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static bool IsExecutableRunning(string executablePath)
    {
        string expected = Path.GetFullPath(executablePath);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null
                        && Path.GetFullPath(path).Equals(
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                }
            }
        }

        return false;
    }

    private static string GetSafetyMessage(OptiScalerSafetyDecision decision) =>
        decision.BlockReason switch
        {
            OptiScalerSafetyBlockReason.GameRunning =>
                "Najpierw zamknij grę.",
            OptiScalerSafetyBlockReason.OfflineUseNotConfirmed =>
                "Potwierdź użycie wyłącznie offline lub w trybie single-player.",
            OptiScalerSafetyBlockReason.AntiCheatDetected =>
                $"Instalacja zablokowana: wykryto anti-cheat ({decision.Evidence}).",
            OptiScalerSafetyBlockReason.ExperimentalUseNotConfirmed =>
                "Potwierdź ryzyko wersji eksperymentalnej OptiScaler.",
            OptiScalerSafetyBlockReason.GpuNotSupported =>
                $"Neural Rendering wymaga GeForce RTX 50 lub nowszej. {decision.Evidence}",
            OptiScalerSafetyBlockReason.DriverTooOld =>
                $"Sterownik NVIDIA jest za stary. {decision.Evidence}",
            OptiScalerSafetyBlockReason.NeuralRenderingModelMissing =>
                "Sterownik nie zawiera modelu nvngx_dlssnr.dll. "
                    + "Zainstaluj sterownik 616.56 lub nowszy.",
            _ => "Instalacja została zablokowana.",
        };

    private static void DeleteEmptyDirectories(
        string targetDirectory,
        IEnumerable<OptiScalerInstalledFile> files)
    {
        foreach (string directory in files
                     .Select(file => Path.GetDirectoryName(
                         GetContainedPath(targetDirectory, file.RelativePath)))
                     .Where(directory => directory is not null)
                     .Select(directory => directory!)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(directory => directory.Length))
        {
            if (!directory.Equals(targetDirectory, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PayloadFile(string SourcePath, string RelativePath);

    private sealed record CachedOptiScalerPackages(
        IReadOnlyList<OptiScalerPackageDescriptor> Packages,
        DateTimeOffset ExpiresAtUtc);

    private sealed record OptiScalerInstalledFile(
        string RelativePath,
        string InstalledSha256,
        string? OriginalBackupRelativePath);

    private sealed record OptiScalerInstallationManifest(
        int SchemaVersion,
        string ProfileId,
        string PackageVersion,
        string TargetExecutablePath,
        string TargetDirectory,
        OptiScalerProxy Proxy,
        DateTimeOffset InstalledAtUtc,
        IReadOnlyList<OptiScalerInstalledFile> Files,
        OptiScalerReleaseChannel Channel = OptiScalerReleaseChannel.Stable);
}

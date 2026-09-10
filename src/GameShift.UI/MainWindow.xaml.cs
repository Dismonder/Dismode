using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameShift.Contracts.Protocol;
using GameShift.Core.Activation;
using GameShift.Core.Cpu;
using GameShift.Core.OptiScaler;
using GameShift.Core.Product;
using GameShift.Core.Profiles;
using GameShift.Core.Updates;
using GameShift.Data.Journal;
using GameShift.Data.Storage;
using GameShift.Data.UserData;
using GameShift.UI.Services;
using GameShift.UI.ViewModels;
using GameShift.Windows.Cpu;
using GameShift.Windows.OptiScaler;
using GameShift.Windows.Platform;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;
using GameShift.Windows.Security;
using GameShift.Windows.Sessions;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WindowsColor = global::Windows.UI.Color;

namespace GameShift.UI;

public sealed partial class MainWindow : Window, IDisposable
{
    private const int MaximumRecentHistoryItems = 100;
    private const int DashboardFrameHistoryLimit = 90;
    private const int StandardMaximumBackgroundProcesses = 32;
    private const int AggressiveMaximumBackgroundProcesses = 64;
    private static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    private static readonly HashSet<string> BlockedShellLaunchNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "GameShift",
        "GameShift.UI",
        "GameShift.SessionHost",
        "GameShift.SystemAgent",
        "PresentMon-2.5.1-x64",
        "cmd",
        "conhost",
        "cscript",
        "dwm",
        "explorer",
        "lsass",
        "mshta",
        "powershell",
        "pwsh",
        "regedit",
        "regsvr32",
        "rundll32",
        "services",
        "smss",
        "svchost",
        "taskmgr",
        "wininit",
        "winlogon",
        "wscript",
        "BEService",
        "EasyAntiCheat",
        "EasyAntiCheat_EOS",
        "EAAntiCheat.GameServiceLauncher",
        "vgc",
        "vgtray",
    };

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SqliteUserDataStore _userDataStore = new();
    private readonly ManualGameProfileFactory _profileFactory = new();
    private readonly GameLibrarySyncService _gameLibrarySync = new();
    private readonly DiagnosticsProbeService _diagnostics;
    private readonly SessionClientService _sessions;
    private readonly UpdateClientService _updates;
    private readonly MemoryOptimizerComponentService _memoryOptimizer;
    private readonly SystemOptimizerComponentService _systemOptimizer;
    private readonly OptiScalerManager _optiScaler = new();
    private readonly NvidiaDriverStoreProbe _driverStoreProbe = new();
    private readonly MemoryOptimizerGameStateExporter
        _memoryOptimizerGameStateExporter;
    private readonly LocalSystemMetricsSampler _systemMetrics = new();
    private readonly UiActivationServer _activationServer;
    private readonly TrayIconService? _trayIcon;
    private readonly SemaphoreSlim _externalLaunchGate = new(1, 1);
    private readonly GameShiftLaunchOptions _launchOptions;
    private readonly string _userSid;
    private readonly ImageSource? _dashboardFallbackArtwork;
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private readonly ObservableCollection<ProfileListItem> _libraryView = [];
    private readonly ObservableCollection<LibraryShelf> _libraryShelves = [];
    private ProfileListItem? _libraryHero;
    private readonly Queue<double> _dashboardFrameTimes = [];
    private readonly ObservableCollection<HistoryListItem> _history = [];
    private readonly ObservableCollection<SessionPlanActionListItem>
        _planActions = [];
    private readonly ObservableCollection<BackgroundApplicationListItem>
        _backgroundApplications = [];
    private readonly ObservableCollection<ServiceClassificationListItem>
        _serviceClassifications = [];
    private readonly Queue<UiActivationRequest> _pendingActivationRequests = [];
    private readonly DispatcherTimer _sessionPollTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
    private readonly DispatcherTimer _overlaySettingsSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(400),
    };
    private SessionPlanClientSnapshot? _pendingPlan;
    private SessionStateClientSnapshot? _activeSession;
    private PerformanceOverlayWindow? _performanceOverlay;
    private ForegroundGameWatcher? _foregroundGameWatcher;
    private readonly DispatcherTimer _overlayPollTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500),
    };
    private bool _overlayPollInFlight;
    private GameOptimizationPreferences? _loadedPreferences;
    private UpdatePreferences? _updatePreferences;
    private SignedUpdateManifest? _availableUpdate;
    private string? _stagedInstallerPath;
    private Guid? _dashboardFrameSessionId;
    private bool _isLoaded;
    private bool _isBusy;
    private bool _isSessionPollRunning;
    private bool _isSynchronizingProfileSelection;
    private bool _isLoadingOptimizationPreferences;
    private bool _isLoadingOverlayPreferences;
    private bool _suppressFpsSettingEvents;
    private bool _isOverlaySettingsSaveRunning;
    private int _frameRateTrackingUpdateRevision;
    private bool _isLoadingUpdatePreferences;
    private bool _isUpdateOperationRunning;
    private bool _deferredStartupPending;
    private bool _isDeferredStartupRunning;
    private bool _automaticUpdateDeferredForSession;
    private bool _sessionEndpointAvailable;
    private bool? _isRecoveryJournalClean;
    private ProfileListItem? _detectedRunningUnoptimizedGame;
    private int _detectedRunningProcessId;
    private bool _disposed;

    public MainWindow()
        : this(new(
            StartInBackground: false,
            GameExecutablePath: null))
    {
    }

    public MainWindow(GameShiftLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions
            ?? throw new ArgumentNullException(nameof(launchOptions));
        InitializeComponent();
        _dashboardFallbackArtwork = DashboardHeroImage.Source;
        NavigateToPage("dashboard");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragRegion);
        SystemBackdrop = new MicaBackdrop();
        Title = ProductInformation.DisplayName;
        _userSid = CurrentWindowsIdentity.GetUserSid().Value;
        _diagnostics = new(_userSid);
        _sessions = new(_userSid);
        _updates = new(
            _userDataStore,
            GameShiftStoragePaths.UserUpdatesDirectory);
        _memoryOptimizer = new(_userSid);
        _systemOptimizer = new(_userSid);
        _memoryOptimizerGameStateExporter = new(_userSid);
        LibraryShelvesSource.Source = _libraryShelves;
        ProfilesList.ItemsSource = LibraryShelvesSource.View;
        _profiles.CollectionChanged += (_, _) => ApplyLibraryFilter();
        ApplyLibraryFilter();
        DashboardGameRail.ItemsSource = _profiles;
        GameProfileSelector.ItemsSource = _profiles;
        HistoryList.ItemsSource = _history;
        PlanActionsList.ItemsSource = _planActions;
        BackgroundApplicationsList.ItemsSource =
            _backgroundApplications;
        ServiceClassificationsList.ItemsSource =
            _serviceClassifications;
        _sessionPollTimer.Tick += OnSessionPollTick;
        _overlaySettingsSaveTimer.Tick += OnOverlaySettingsSaveTick;
        _overlayPollTimer.Tick += OnOverlayPollTick;
        string windowIconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "GameShift.ico");
        if (File.Exists(windowIconPath))
        {
            AppWindow.SetIcon(windowIconPath);
            try
            {
                _trayIcon = new(
                    WinRT.Interop.WindowNative.GetWindowHandle(this),
                    windowIconPath);
                _trayIcon.OpenRequested += OnTrayOpenRequested;
                _trayIcon.ExitRequested += OnTrayExitRequested;
            }
            catch
            {
                // Fallback gracefully if tray icon cannot be attached
            }
        }

        _activationServer = new(
            _userSid,
            request => DispatcherQueue.TryEnqueue(
                () => HandleActivationRequest(request)));

        if (!_launchOptions.StartInBackground
            && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        else
        {
            AppWindow.Resize(new(1240, 820));
        }

        UpdateTitleBarTheme(ElementTheme.Dark);
        UpdateDashboardSystemMetrics();
        DiagnosticsProtocolText.Text =
            $"v{ProtocolInfo.CurrentVersion}; limit "
            + $"{ProtocolInfo.MaximumMessageBytes / 1024} KiB";
        DiagnosticsPlatformText.Text =
            WindowsPlatformSupport.DescribeCurrentSystem();
        DiagnosticsUserText.Text = RedactSid(_userSid);
        UpdateGamingDiagnostics();
        UpdatesCurrentVersionText.Text =
            ProductInformation.FullDisplayName;
        SettingsVersionText.Text = ProductInformation.FullDisplayName;
        UpdateServiceAddressText.Text =
            new Uri(ProductInformation.UpdateServiceBaseUri).Host;
        Closed += OnWindowClosed;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        // Let the initial WinUI Loaded/layout pass finish before startup I/O.
        await Task.Yield();

        await LoadPerformanceOverlayPreferencesAsync(_lifetime.Token);
        await LoadUpdatePreferencesAsync(_lifetime.Token);

        // Odczyt topologii dotyka rejestru i CPU sets, wiec nie na watku UI.
        await Task.Run(
            () =>
            {
                CpuTopology? topology = SystemCpuTopologyProvider.Read();
                DispatcherQueue.TryEnqueue(
                    () => ApplyCpuTopologyDescription(topology));
            },
            _lifetime.Token);

        if (_launchOptions.GameExecutablePath is string executablePath)
        {
            await _userDataStore.InitializeAsync(_lifetime.Token);
            await LoadProfilesAsync(_lifetime.Token);
            await RefreshActiveSessionCoreAsync(_lifetime.Token);
            await RefreshDashboardRecoveryStatusAsync(_lifetime.Token);
            if (!_lifetime.IsCancellationRequested)
            {
                _sessionPollTimer.Start();
                await LaunchExternalGameAsync(
                    executablePath,
                    keepWindowHidden: _launchOptions.StartInBackground,
                    _lifetime.Token);
                _deferredStartupPending = true;
                _ = CompleteDeferredStartupAsync(_lifetime.Token);
                DrainPendingActivationRequests();
            }

            return;
        }

        await SyncGameLibraryAsync(
            showResultWhenUnchanged: false,
            _lifetime.Token);
        if (!_lifetime.IsCancellationRequested)
        {
            await RefreshAllAsync(_lifetime.Token);
            await RefreshMemoryOptimizerStatusAsync(_lifetime.Token);
            await RefreshSystemOptimizerStatusAsync(_lifetime.Token);
        }

        if (!_lifetime.IsCancellationRequested)
        {
            _sessionPollTimer.Start();
            _ = CheckForUpdatesAsync(
                manual: false,
                _lifetime.Token);
            DrainPendingActivationRequests();
        }
    }

    public void HideToTray()
    {
        AppWindow.Hide();
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private void OnTrayOpenRequested(object? sender, EventArgs args) =>
        _ = DispatcherQueue.TryEnqueue(ShowFromTray);

    private void OnTrayExitRequested(object? sender, EventArgs args) =>
        _ = DispatcherQueue.TryEnqueue(
            () => _ = ShutdownGamingComponentsAsync());

    private async Task ShutdownGamingComponentsAsync()
    {
        try
        {
            RecoveryJournalInspection journal =
                await RecoveryJournalInspector.InspectAsync(
                    GameShiftStoragePaths.UserRecoveryJournalPath,
                    _lifetime.Token);
            if (!journal.IsClean || _activeSession is not null ||
                _pendingPlan is not null)
            {
                ShowFromTray();
                _trayIcon?.ShowNotification(
                    "GameShift — wyłączenie zablokowane",
                    !journal.IsClean
                        ? "Najpierw zakończ recovery aktywnej sesji."
                        : "Najpierw zakończ aktywną sesję lub przygotowany plan.");
                return;
            }

            SessionShutdownReadinessClientSnapshot readiness =
                await _sessions.ShutdownComponentsAsync(_lifetime.Token);
            if (!readiness.CanShutdown)
            {
                ShowFromTray();
                _trayIcon?.ShowNotification(
                    "GameShift — wyłączenie zablokowane",
                    readiness.Message);
                return;
            }

            string helperPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "GameShift.SessionHost.exe"));
            if (!File.Exists(helperPath) ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetDirectoryName(helperPath)?.TrimEnd(
                        Path.DirectorySeparatorChar),
                    Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(
                        Path.DirectorySeparatorChar)))
            {
                throw new FileNotFoundException(
                    "Brakuje bezpiecznego helpera zamykania GameShift.",
                    helperPath);
            }

            using Process? helper = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = "--shutdown-components",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (helper is null)
            {
                throw new InvalidOperationException(
                    "Windows nie uruchomił bezpiecznej bramy zamykania.");
            }

            await helper.WaitForExitAsync(_lifetime.Token);
            if (helper.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Bezpieczna brama nie zatrzymała składników GameShift " +
                    $"(kod {helper.ExitCode}).");
            }

            Close();
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowFromTray();
            _trayIcon?.ShowNotification(
                "GameShift — nie wyłączono",
                exception.Message);
        }
    }

    private void HandleActivationRequest(UiActivationRequest request)
    {
        if (!_isLoaded)
        {
            if (_pendingActivationRequests.Count < 8)
            {
                _pendingActivationRequests.Enqueue(request);
            }

            return;
        }

        if (!request.KeepWindowHidden)
        {
            ShowFromTray();
        }

        _ = LaunchExternalGameAsync(
            request.GameExecutablePath,
            request.KeepWindowHidden,
            _lifetime.Token);
    }

    private void DrainPendingActivationRequests()
    {
        while (_pendingActivationRequests.TryDequeue(
                   out UiActivationRequest? request))
        {
            HandleActivationRequest(request);
        }
    }

    private async Task LaunchExternalGameAsync(
        string executablePath,
        bool keepWindowHidden,
        CancellationToken cancellationToken)
    {
        await _externalLaunchGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForUiIdleAsync(cancellationToken);
            SetBusy(true);
            ProfileListItem selected;
            try
            {
                ManualGameProfile profile =
                    await EnsureShellGameProfileAsync(
                        executablePath,
                        cancellationToken);
                await LoadProfilesAsync(cancellationToken);
                selected = _profiles.First(item =>
                    item.Profile.ProfileId == profile.ProfileId);
            }
            finally
            {
                SetBusy(false);
            }

            await LaunchProfileThroughGameShiftAsync(
                selected,
                keepWindowHidden,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Error,
                "Nie uruchomiono przez GameShift",
                exception.Message);
            _trayIcon?.ShowNotification(
                "GameShift — żądanie odrzucone",
                exception.Message);
        }
        finally
        {
            _externalLaunchGate.Release();
        }
    }

    private async ValueTask<ManualGameProfile> EnsureShellGameProfileAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        string fullPath = GameShiftLaunchOptions.NormalizeExecutablePath(
            executablePath);
        ValidateShellGameTarget(fullPath);

        IReadOnlyList<ManualGameProfile> profiles =
            await _userDataStore.ListAsync(cancellationToken);
        ManualGameProfile? existing = profiles.FirstOrDefault(profile =>
            StringComparer.OrdinalIgnoreCase.Equals(
                profile.ExecutablePath,
                fullPath));
        if (existing is null)
        {
            ManualGameProfile created = await _profileFactory.CreateAsync(
                Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                launchArguments: [],
                OptimizationPreset.Safe,
                cancellationToken);
            await _userDataStore.UpsertAsync(created, cancellationToken);
            return created;
        }

        if (!existing.IsEnabled)
        {
            throw new InvalidOperationException(
                "Profil tej gry jest wyłączony w bibliotece GameShift.");
        }

        string currentHash = await ExecutableFileHasher.ComputeSha256Async(
            fullPath,
            cancellationToken);
        if (StringComparer.OrdinalIgnoreCase.Equals(
                currentHash,
                existing.ExecutableSha256))
        {
            return existing;
        }

        ManualGameProfile refreshed = new(
            existing.ProfileId,
            existing.DisplayName,
            fullPath,
            currentHash,
            Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException(
                    "Plik gry nie ma katalogu roboczego."),
            existing.LaunchArguments,
            existing.Preset,
            isEnabled: true,
            existing.CreatedAtUtc,
            DateTimeOffset.UtcNow,
            existing.ArtworkPath);
        await _userDataStore.UpsertAsync(refreshed, cancellationToken);
        return refreshed;
    }

    private static void ValidateShellGameTarget(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "Wybrany plik gry nie istnieje.",
                executablePath);
        }

        string executableName = Path.GetFileNameWithoutExtension(
            executablePath);
        if (BlockedShellLaunchNames.Contains(executableName)
            || IsInsideDirectory(
                executablePath,
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows))
            || IsInsideDirectory(executablePath, AppContext.BaseDirectory))
        {
            throw new UnauthorizedAccessException(
                "Ten plik jest składnikiem systemu, zabezpieczeń albo "
                + "GameShift i nie może być uruchomiony jako gra.");
        }
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(
            fullDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task WaitForUiIdleAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline =
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (_isBusy && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }

        if (_isBusy)
        {
            throw new TimeoutException(
                "GameShift kończy inną operację. Ponów uruchomienie gry.");
        }
    }

    private async Task CompleteDeferredStartupAsync(
        CancellationToken cancellationToken)
    {
        if (_isDeferredStartupRunning || !_deferredStartupPending)
        {
            return;
        }

        if (_activeSession is not null)
        {
            return;
        }

        _isDeferredStartupRunning = true;
        try
        {
            await WaitForUiIdleAsync(cancellationToken);
            await SyncGameLibraryAsync(
                showResultWhenUnchanged: false,
                cancellationToken);
            await LoadHistoryAsync(cancellationToken);
            await RefreshDiagnosticsCoreAsync(cancellationToken);
            _deferredStartupPending = false;
            _ = CheckForUpdatesAsync(
                manual: false,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            _trayIcon?.ShowNotification(
                "GameShift — inicjalizacja",
                exception.Message);
        }
        finally
        {
            _isDeferredStartupRunning = false;
        }
    }

    private async Task CompleteDeferredAutomaticUpdateAsync(
        CancellationToken cancellationToken)
    {
        if (!_automaticUpdateDeferredForSession
            || _isUpdateOperationRunning
            || _availableUpdate is null
            || _updatePreferences?.AutomaticInstallEnabled != true)
        {
            return;
        }

        _automaticUpdateDeferredForSession = false;
        _isUpdateOperationRunning = true;
        UpdateUpdateControls();
        try
        {
            await DownloadAvailableUpdateCoreAsync(cancellationToken);
            await LaunchVerifiedInstallerCoreAsync(
                automatic: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Aktualizacja oczekuje",
                "Pakiet jest gotowy, ale instalacja nie otrzymała zgody "
                + "administratora.");
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Automatyczna aktualizacja oczekuje",
                exception.Message);
        }
        finally
        {
            _isUpdateOperationRunning = false;
            UpdateUpdateControls();
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs args) =>
        await RefreshAllAsync(_lifetime.Token);

    private async void OnRefreshDiagnosticsClicked(
        object sender,
        RoutedEventArgs args)
    {
        await RefreshDiagnosticsAsync(_lifetime.Token);
        await RefreshMemoryOptimizerStatusAsync(_lifetime.Token);
        await RefreshSystemOptimizerStatusAsync(_lifetime.Token);
    }

    private async void OnOpenMemoryOptimizerClicked(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            await _memoryOptimizer.OpenAsync(_lifetime.Token);
            await RefreshMemoryOptimizerStatusAsync(_lifetime.Token);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            MemoryOptimizerStatusText.Text = "Nie można uruchomić";
            MemoryOptimizerDetailsText.Text = exception.Message;
        }
    }

    private async void OnOpenSystemOptimizerClicked(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            await _systemOptimizer.OpenAsync(_lifetime.Token);
            await RefreshSystemOptimizerStatusAsync(_lifetime.Token);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            SystemOptimizerStatusText.Text = "Nie można uruchomić";
            SystemOptimizerProfileText.Text = exception.Message;
        }
    }

    private async void OnDiscoverGamesClicked(
        object sender,
        RoutedEventArgs args) =>
        await SyncGameLibraryAsync(
            showResultWhenUnchanged: true,
            _lifetime.Token);

    private void OnOpenGameModeClicked(
        object sender,
        RoutedEventArgs args)
    {
        NavigateToPage("plan");
    }

    private void OnOpenProfilesClicked(
        object sender,
        RoutedEventArgs args)
    {
        NavigateToPage("profiles");
    }

    private async void OnLaunchSelectedFromDashboardClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        if (ProfilesList.SelectedItem is not ProfileListItem selected)
        {
            NavigateToPage("profiles");
            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Warning,
                "Wybierz grę",
                "Biblioteka nie zawiera jeszcze wybranego profilu gry.");
            return;
        }

        await LaunchProfileThroughGameShiftAsync(
            selected,
            keepWindowHidden: false,
            _lifetime.Token);
    }

    private void OnFpsOverlaySettingChanged(
        object sender,
        RoutedEventArgs args) =>
        ApplyFpsOverlaySettingFromToggle(sender);

    /// <summary>
    /// Fills in what the machine actually reports, so the setting below is read
    /// against a fact rather than a hope. On a CPU whose cores are all the same
    /// speed there is nothing to pin a game to, and saying so plainly is more
    /// use than an option that silently does nothing.
    /// </summary>
    private void ApplyCpuTopologyDescription(CpuTopology? topology)
    {
        if (topology is null || topology.Processors.Count == 0)
        {
            CpuTopologyText.Text =
                "Nie udało się odczytać topologii procesora.";
            return;
        }

        int physical = CpuTopology.CountPhysicalCores(topology.Processors);
        if (!topology.IsHybrid)
        {
            // Przypinanie gry rzeczywiscie nic tu nie daje. Przypinanie tla
            // daje duzo i dlugo mialem to zle — wiec opis musi rozrozniac te
            // dwie rzeczy, zamiast konczyc sie na "GameShift tego nie robi".
            CpuAffinityDecision background = CpuAffinityPolicy.Decide(
                topology,
                CpuAffinityRole.Background);
            CpuTopologyText.Text =
                $"{physical} rdzeni, {topology.Processors.Count} wątków, "
                + "wszystkie tej samej klasy wydajności. Przypinanie gry do "
                + "rdzeni nic by tu nie zmieniło. "
                + (background.ShouldApply
                    ? "Za to procesy w tle, które zaczną zjadać procesor, "
                        + "GameShift zamknie w "
                        + $"{System.Numerics.BitOperations.PopCount(background.Mask)}"
                        + " wątkach i zostawi grze resztę."
                    : background.Explanation);
            return;
        }

        int performance =
            CpuTopology.CountPhysicalCores(topology.PerformanceCores);
        int efficiency =
            CpuTopology.CountPhysicalCores(topology.EfficiencyCores);
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            topology,
            CpuAffinityRole.Foreground);
        CpuTopologyText.Text =
            $"{performance} rdzeni wydajnych i {efficiency} energooszczędnych, "
            + $"{topology.Processors.Count} wątków. {decision.Explanation}";
    }

    private void OnProBalanceSettingChanged(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        // Ustawienie dotyczy nastepnej sesji: zmiana w trakcie trwajacej
        // oznaczalaby wlaczenie lub wylaczenie nadzorcy w polowie, a on trzyma
        // stan tego, co juz ograniczyl.
        ProBalanceDescriptionText.Text = toggle.IsOn
            ? "Zadziała od następnej sesji. GameShift obniża priorytet "
                + "procesom, które zaczynają zjadać procesor już w trakcie "
                + "gry, i oddaje go po sesji. Powłoki, anti-cheat i samej gry "
                + "nie dotyka."
            : "GameShift obniża priorytet procesom, które zaczynają zjadać "
                + "procesor już w trakcie gry, i oddaje go po sesji. Powłoki, "
                + "anti-cheat i samej gry nie dotyka.";
    }

    private async void OnFpsTrackingSettingChanged(
        object sender,
        RoutedEventArgs args)
    {
        if (_suppressFpsSettingEvents)
        {
            return;
        }

        bool enabled = sender is ToggleSwitch toggle
            ? toggle.IsOn
            : FpsTrackingToggleSwitch.IsOn;
        SetFpsTrackingToggleValues(enabled);
        ApplyOverlaySettingsFromControls(scheduleSave: true);
        if (_isLoadingOverlayPreferences
            || !_isLoaded
            || _activeSession is null
            || !_sessionEndpointAvailable
            || _lifetime.IsCancellationRequested)
        {
            return;
        }

        int revision = ++_frameRateTrackingUpdateRevision;
        try
        {
            SessionStateClientSnapshot updated =
                await _sessions.SetFrameRateTrackingAsync(
                    _activeSession.SessionId,
                    enabled,
                    _lifetime.Token);
            if (revision != _frameRateTrackingUpdateRevision
                || _activeSession?.SessionId != updated.SessionId)
            {
                return;
            }

            _activeSession = updated;
            ApplyActiveSession(updated);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Warning,
                "Nie zmieniono pomiaru FPS",
                exception.Message);
        }
    }

    private void ApplyFpsOverlaySettingFromToggle(object sender)
    {
        if (_suppressFpsSettingEvents)
        {
            return;
        }

        bool enabled = sender is ToggleSwitch toggle
            ? toggle.IsOn
            : FpsOverlayToggleSwitch.IsOn;
        SetFpsOverlayToggleValues(enabled);

        ApplyOverlaySettingsFromControls(scheduleSave: true);
    }

    private void SetFpsTrackingToggleValues(bool enabled)
    {
        if (FpsTrackingToggleSwitch is null
            || FpsTrackingSettingsToggleSwitch is null)
        {
            return;
        }

        _suppressFpsSettingEvents = true;
        try
        {
            FpsTrackingToggleSwitch.IsOn = enabled;
            FpsTrackingSettingsToggleSwitch.IsOn = enabled;
        }
        finally
        {
            _suppressFpsSettingEvents = false;
        }
    }

    private void SetFpsOverlayToggleValues(bool enabled)
    {
        if (FpsOverlayToggleSwitch is null
            || FpsOverlaySettingsToggleSwitch is null)
        {
            return;
        }

        _suppressFpsSettingEvents = true;
        try
        {
            FpsOverlayToggleSwitch.IsOn = enabled;
            FpsOverlaySettingsToggleSwitch.IsOn = enabled;
        }
        finally
        {
            _suppressFpsSettingEvents = false;
        }
    }

    private void OnFpsOverlayStyleChanged(
        object sender,
        SelectionChangedEventArgs args) =>
        ApplyOverlaySettingsFromControls(scheduleSave: true);

    private void OnFpsOverlayThemeChanged(
        object sender,
        SelectionChangedEventArgs args) =>
        ApplyOverlaySettingsFromControls(scheduleSave: true);

    private void OnFpsOverlayValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs args) =>
        ApplyOverlaySettingsFromControls(scheduleSave: true);

    private void OnFpsOverlayCornerChanged(
        object sender,
        SelectionChangedEventArgs args) =>
        ApplyOverlaySettingsFromControls(scheduleSave: true);

    private void OnSfxToggled(
        object sender,
        RoutedEventArgs args)
    {
        TacticalAudioService.Instance.IsEnabled = SfxToggleSwitch.IsOn;
        if (SfxToggleSwitch.IsOn)
        {
            TacticalAudioService.Instance.PlayToggle();
        }
    }

    private void OnVfxToggled(
        object sender,
        RoutedEventArgs args)
    {
        TacticalVfxService.Instance.IsEnabled = VfxToggleSwitch.IsOn;
        if (VfxToggleSwitch.IsOn)
        {
            TacticalVfxService.Instance.AnimatePulse(VfxToggleSwitch);
        }
    }

    private void OnOptimizationIntensitySelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        TacticalAudioService.Instance.PlayToggle();
        if (AggressiveModeInfoBar is null)
        {
            return;
        }

        AggressiveModeInfoBar.Visibility = IsAggressiveOptimizationEnabled()
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_backgroundApplications.Count > 0)
        {
            _ = ApplyBackgroundRules();
            ClearPreparedPlan();
            UpdateBackgroundApplicationSummary();
        }
    }

    private void OnTopNavigationClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: string selectedTag }
            && DashboardPage is not null)
        {
            NavigateToPage(selectedTag);
        }
    }

    private void NavigateToPage(string selectedTag)
    {
        TacticalAudioService.Instance.PlayClick();
        ApplyPageVisibility(selectedTag);
        DashboardNavigationIndicator.Opacity = selectedTag == "dashboard"
            ? 1
            : 0;
        ProfilesNavigationIndicator.Opacity = selectedTag == "profiles"
            ? 1
            : 0;
        PlanNavigationIndicator.Opacity = selectedTag == "plan" ? 1 : 0;
        HistoryNavigationIndicator.Opacity = selectedTag == "history" ? 1 : 0;
        UpdatesNavigationIndicator.Opacity = selectedTag == "updates" ? 1 : 0;
        DiagnosticsNavigationIndicator.Opacity = selectedTag == "diagnostics"
            ? 1
            : 0;
    }

    private void ApplyPageVisibility(string selectedTag)
    {
        DashboardPage.Visibility = selectedTag == "dashboard"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfilesPage.Visibility = selectedTag == "profiles"
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlanPage.Visibility = selectedTag == "plan"
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryPage.Visibility = selectedTag == "history"
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatesPage.Visibility = selectedTag == "updates"
            ? Visibility.Visible
            : Visibility.Collapsed;
        DiagnosticsPage.Visibility = selectedTag == "diagnostics"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnCheckForUpdatesClicked(
        object sender,
        RoutedEventArgs args) =>
        await CheckForUpdatesAsync(
            manual: true,
            _lifetime.Token);

    private async void OnCheckForUpdatesInSettingsClicked(
        object sender,
        RoutedEventArgs args)
    {
        TacticalAudioService.Instance.PlayClick();
        NavigateToPage("updates");
        await CheckForUpdatesAsync(
            manual: true,
            _lifetime.Token);
    }

    private async void OnAutomaticUpdateSettingChanged(
        object sender,
        RoutedEventArgs args)
    {
        if (_isLoadingUpdatePreferences
            || _isUpdateOperationRunning
            || AutomaticUpdateChecksToggle is null
            || AutomaticUpdateInstallToggle is null)
        {
            return;
        }

        UpdatePreferences current =
            _updatePreferences ?? UpdatePreferences.CreateDefault();
        UpdatePreferences updated = current with
        {
            AutomaticChecksEnabled = AutomaticUpdateChecksToggle.IsOn,
            AutomaticInstallEnabled = AutomaticUpdateInstallToggle.IsOn,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            await _userDataStore.SaveUpdatePreferencesAsync(
                updated,
                _lifetime.Token);
            _updatePreferences = updated;
            UpdateLastCheckText(updated);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Error,
                "Nie zapisano ustawień aktualizacji",
                exception.Message);
        }
    }

    private async void OnDownloadUpdateClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isUpdateOperationRunning || _availableUpdate is null)
        {
            return;
        }

        _isUpdateOperationRunning = true;
        UpdateUpdateControls();
        try
        {
            await DownloadAvailableUpdateCoreAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Error,
                "Nie pobrano aktualizacji",
                exception.Message);
        }
        finally
        {
            _isUpdateOperationRunning = false;
            UpdateUpdateControls();
        }
    }

    private async void OnInstallUpdateClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isUpdateOperationRunning)
        {
            return;
        }

        _isUpdateOperationRunning = true;
        UpdateUpdateControls();
        try
        {
            await LaunchVerifiedInstallerCoreAsync(
                automatic: false,
                _lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Instalacja anulowana",
                "System Windows nie otrzymał zgody administratora.");
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Error,
                "Nie uruchomiono aktualizacji",
                exception.Message);
        }
        finally
        {
            _isUpdateOperationRunning = false;
            UpdateUpdateControls();
        }
    }

    private void OnProfileSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_isSynchronizingProfileSelection)
        {
            return;
        }

        _isSynchronizingProfileSelection = true;
        GameProfileSelector.SelectedItem = ProfilesList.SelectedItem;
        DashboardGameRail.SelectedItem = ProfilesList.SelectedItem;
        _isSynchronizingProfileSelection = false;
        HandleGameProfileChanged();
    }

    /// <summary>
    /// Groups the filtered library into shelves. Profiles that are switched
    /// off come first under one heading, so a game that will not launch is
    /// not buried among the ones that will; everything else groups by store,
    /// largest first.
    /// </summary>
    private void RebuildLibraryShelves()
    {
        _libraryShelves.Clear();

        List<ProfileListItem> needsAttention = [.. _libraryView
            .Where(item => !item.Profile.IsEnabled)
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)];
        if (needsAttention.Count > 0)
        {
            _libraryShelves.Add(new("Wymaga uwagi", needsAttention));
        }

        IEnumerable<IGrouping<string, ProfileListItem>> bySource = _libraryView
            .Where(item => item.Profile.IsEnabled)
            .GroupBy(item => item.SourceLabel, StringComparer.CurrentCulture);
        foreach (IGrouping<string, ProfileListItem> group in bySource
                     .OrderByDescending(group => group.Count())
                     .ThenBy(group => group.Key, StringComparer.CurrentCulture))
        {
            _libraryShelves.Add(new(
                group.Key,
                group
                    .OrderByDescending(item =>
                        item.LastPlayedAtUtc ?? DateTimeOffset.MinValue)
                    .ThenBy(
                        item => item.DisplayName,
                        StringComparer.CurrentCulture)));
        }
    }

    /// <summary>
    /// Puts the most recently played game at the top of the library, so the
    /// page opens on the one action a player most likely came to take.
    /// </summary>
    private void UpdateLibraryHero()
    {
        _libraryHero = _libraryView
            .Where(item => item.LastPlayedAtUtc is not null)
            .OrderByDescending(item => item.LastPlayedAtUtc)
            .FirstOrDefault()
            ?? _libraryView.FirstOrDefault();

        if (LibraryHeroPanel is null)
        {
            return;
        }

        if (_libraryHero is null)
        {
            LibraryHeroPanel.Visibility = Visibility.Collapsed;
            return;
        }

        LibraryHeroPanel.Visibility = Visibility.Visible;
        LibraryHeroTitleText.Text = _libraryHero.DisplayName;
        LibraryHeroStateText.Text = _libraryHero.StateLabel;
        LibraryHeroImage.Source = _libraryHero.HeroArtworkSource
            ?? _libraryHero.PosterArtworkSource;
        LibraryHeroMetaText.Text = string.Join(
            "   •   ",
            new[]
            {
                _libraryHero.SourceLabel,
                _libraryHero.LastPlayedLabel,
                _libraryHero.PlaytimeLabel,
                $"Preset: {_libraryHero.PresetLabel}",
            }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private async void OnLibraryHeroLaunchClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy || _libraryHero is not { } hero)
        {
            return;
        }

        await LaunchProfileThroughGameShiftAsync(
            hero,
            keepWindowHidden: false,
            _lifetime.Token);
    }

    private void OnLibraryHeroPlanClicked(object sender, RoutedEventArgs args)
    {
        if (_libraryHero is not { } hero)
        {
            return;
        }

        // Plan gry czyta zaznaczenie z biblioteki, wiec musi ono wskazywac
        // gre z sekcji hero, zanim przejdziemy na tamta zakladke.
        RevealProfileInLibrary(hero);
        ProfilesList.SelectedItem = hero;
        NavigateToPage("plan");
    }

    private void OnLibrarySearchTextChanged(
        object sender,
        TextChangedEventArgs args) => ApplyLibraryFilter();

    /// <summary>
    /// Rebuilds the library view from the current search text. Only the
    /// library list is filtered; the dashboard rail and the profile picker
    /// keep showing every game, so a filter never hides the active session.
    /// </summary>
    private void ApplyLibraryFilter()
    {
        string query = LibrarySearchBox?.Text?.Trim() ?? string.Empty;
        ProfileListItem? selected = ProfilesList.SelectedItem as ProfileListItem;

        _libraryView.Clear();
        foreach (ProfileListItem item in _profiles)
        {
            if (query.Length == 0
                || item.DisplayName.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase))
            {
                _libraryView.Add(item);
            }
        }

        RebuildLibraryShelves();
        UpdateLibraryHero();

        if (selected is not null && _libraryView.Contains(selected))
        {
            ProfilesList.SelectedItem = selected;
        }

        if (LibrarySearchSummary is null)
        {
            return;
        }

        LibrarySearchSummary.Text = query.Length == 0
            ? _profiles.Count switch
            {
                0 => string.Empty,
                1 => "1 gra",
                _ => $"{_profiles.Count} gier",
            }
            : $"{_libraryView.Count} z {_profiles.Count}";
    }

    /// <summary>
    /// Clears the search when a selection made elsewhere points at a game the
    /// filter is hiding, so the library never appears to ignore the choice.
    /// </summary>
    private void RevealProfileInLibrary(ProfileListItem? item)
    {
        if (item is null
            || _libraryView.Contains(item)
            || LibrarySearchBox is null)
        {
            return;
        }

        LibrarySearchBox.Text = string.Empty;
        ApplyLibraryFilter();
    }

    private void OnDashboardGameRailSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_isSynchronizingProfileSelection)
        {
            return;
        }

        _isSynchronizingProfileSelection = true;
        RevealProfileInLibrary(DashboardGameRail.SelectedItem as ProfileListItem);
        ProfilesList.SelectedItem = DashboardGameRail.SelectedItem;
        GameProfileSelector.SelectedItem = DashboardGameRail.SelectedItem;
        _isSynchronizingProfileSelection = false;
        HandleGameProfileChanged();
    }

    private void OnGameProfileSelectorChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_isSynchronizingProfileSelection)
        {
            return;
        }

        _isSynchronizingProfileSelection = true;
        RevealProfileInLibrary(GameProfileSelector.SelectedItem as ProfileListItem);
        ProfilesList.SelectedItem = GameProfileSelector.SelectedItem;
        DashboardGameRail.SelectedItem = GameProfileSelector.SelectedItem;
        _isSynchronizingProfileSelection = false;
        HandleGameProfileChanged();
    }

    private void OnGamePrioritySelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_isLoadingOptimizationPreferences
            && _pendingPlan is not null)
        {
            ClearPreparedPlan();
        }
    }

    private void HandleGameProfileChanged()
    {
        if (_pendingPlan is not null)
        {
            ClearPreparedPlan();
        }

        _backgroundApplications.Clear();
        _serviceClassifications.Clear();
        ServiceClassificationSummaryText.Text =
            "Klasyfikacja usług pojawi się po analizie.";
        ServiceClassificationsList.Visibility =
            Visibility.Collapsed;
        UpdateBackgroundApplicationSummary();
        UpdateDashboardRailSelection();
        UpdateDashboardHero();
        UpdateSessionControls();
        LoadSelectedOptimizationPreferences();
    }

    private async void OnAnalyzeBackgroundApplicationsClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        if (ProfilesList.SelectedItem is not ProfileListItem selected)
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Najpierw wybierz grę",
                "Analiza wyklucza proces wybranej gry i jej krytyczne launchery.");
            return;
        }

        try
        {
            SetBusy(true);
            Task<IReadOnlyList<DiscoveredProcessClientSnapshot>>
                processesTask =
                    _diagnostics.DiscoverProcessesAsync(
                        _lifetime.Token);
            Task<IReadOnlyList<DiscoveredServiceClientSnapshot>>
                servicesTask =
                    _diagnostics.DiscoverServicesAsync(
                        _lifetime.Token);
            IReadOnlyList<DiscoveredProcessClientSnapshot> discovered =
                await processesTask;
            int requiredCount = discovered.Count(process =>
                process.SafetyClassification
                    is "RequiredSystem" or "Unsupported");
            int relatedCount = discovered.Count(process =>
                process.SafetyClassification
                    == "GameInfrastructure");
            int optionalCount = discovered.Count(process =>
                process.SafetyClassification == "OptionalUser");
            ProcessClassificationSummaryText.Text =
                $"Chronione i systemowe: {requiredCount}  •  "
                + $"powiązane z grami: {relatedCount}  •  "
                + $"opcjonalne użytkownika: {optionalCount}";
            int currentSessionId =
                Process.GetCurrentProcess().SessionId;
            string gamePath = selected.Profile.ExecutablePath;
            HashSet<string> duplicatePaths = discovered
                .Where(process =>
                    process.HasMainWindow
                    && !string.IsNullOrWhiteSpace(
                        process.ExecutablePath))
                .GroupBy(
                    process => process.ExecutablePath,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _backgroundApplications.Clear();
            foreach (DiscoveredProcessClientSnapshot process
                         in discovered
                            .Where(process =>
                                IsBackgroundApplicationCandidate(
                                    process,
                                    currentSessionId,
                                    gamePath))
                            .OrderByDescending(process =>
                                process.WorkingSetBytes)
                            .ThenBy(
                                process => process.Name,
                                StringComparer.OrdinalIgnoreCase)
                            .Take(GetMaximumBackgroundProcessCount()))
            {
                bool canClose =
                    process.HasMainWindow
                    && !duplicatePaths.Contains(
                        process.ExecutablePath);
                bool canLowerPriority =
                    process.PriorityClass
                        is "Normal" or "BelowNormal";
                if (!canClose && !canLowerPriority)
                {
                    continue;
                }

                _backgroundApplications.Add(
                    new(
                        process,
                        canClose,
                        canLowerPriority));
            }

            int appliedSavedRuleCount = ApplyBackgroundRules();
            try
            {
                IReadOnlyList<DiscoveredServiceClientSnapshot> services =
                    await servicesTask;
                ApplyServiceClassifications(services);
            }
            catch (Exception exception)
                when (IsExpectedUiFailure(exception))
            {
                _serviceClassifications.Clear();
                ServiceClassificationsList.Visibility =
                    Visibility.Collapsed;
                ServiceClassificationSummaryText.Text =
                    "Usługi: klasyfikacja jest chwilowo niedostępna — "
                    + exception.Message;
            }

            UpdateBackgroundApplicationSummary();
            ShowInfo(
                PlanInfoBar,
                _backgroundApplications.Count > 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Informational,
                _backgroundApplications.Count > 0
                    ? "Analiza zakończona"
                    : "Brak bezpiecznych kandydatów",
                _backgroundApplications.Count > 0
                    ? "Zaznacz tylko aplikacje, których nie potrzebujesz "
                        + "podczas grania i wybierz: zamknij albo obniż "
                        + "priorytet. GameShift nie dotyka procesów chronionych."
                        + (appliedSavedRuleCount > 0
                            ? $" Wczytano zapisane reguły: "
                                + $"{appliedSavedRuleCount}."
                            : string.Empty)
                        + " Nowi kandydaci pozostają niezaznaczeni — "
                        + "wybierz ręcznie pojedyncze działania do testu."
                    : "Nie znaleziono opcjonalnych procesów użytkownika, "
                        + "które spełniają warunki bezpiecznej optymalizacji.");
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Analiza nie powiodła się",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnBackgroundApplicationSelectionChanged(
        object sender,
        RoutedEventArgs args)
    {
        if (_pendingPlan is not null)
        {
            ClearPreparedPlan();
        }

        UpdateBackgroundApplicationSummary();
    }

    private async void OnLaunchGameFromLibraryClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy
            || sender is not FrameworkElement
            {
                DataContext: ProfileListItem selected,
            })
        {
            return;
        }

        await LaunchProfileThroughGameShiftAsync(
            selected,
            keepWindowHidden: false,
            _lifetime.Token);
    }

    private async void OnManageOptiScalerClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy
            || sender is not Button
            {
                DataContext: ProfileListItem selected,
            } button)
        {
            return;
        }

        ProfilesList.SelectedItem = selected;
        button.IsEnabled = false;
        try
        {
            string profileId = selected.Profile.ProfileId.Value.ToString("D");
            OptiScalerInstallationStatus status = _optiScaler.GetStatus(
                profileId);
            Task<IReadOnlyList<OptiScalerReleaseOption>>[] releaseTasks =
            [
                LoadOptiScalerVersionsAsync(
                    OptiScalerReleaseChannel.Stable,
                    _lifetime.Token),
                LoadOptiScalerVersionsAsync(
                    OptiScalerReleaseChannel.Beta,
                    _lifetime.Token),
                LoadOptiScalerVersionsAsync(
                    OptiScalerReleaseChannel.Nightly,
                    _lifetime.Token),
                LoadOptiScalerVersionsAsync(
                    OptiScalerReleaseChannel.DlssNeuralRendering,
                    _lifetime.Token),
            ];
            OptiScalerReleaseOption[] availableReleases =
                (await Task.WhenAll(releaseTasks))
                .SelectMany(releases => releases)
                .ToArray();
            OptiScalerInstallRequest preflightRequest = new(
                profileId,
                selected.Profile.ExecutablePath,
                selected.Profile.WorkingDirectory,
                status.Proxy,
                OfflineUseConfirmed: false);
            OptiScalerInstallPreflight preflight =
                _optiScaler.EvaluateInstall(preflightRequest);
            bool hardBlocked = preflight.Safety.BlockReason is
                OptiScalerSafetyBlockReason.GameRunning
                or OptiScalerSafetyBlockReason.AntiCheatDetected;
            ComboBox proxySelector = BuildOptiScalerProxySelector(status.Proxy);
            ComboBox channelSelector = BuildOptiScalerChannelSelector(
                status.Channel);
            ComboBox versionSelector = new()
            {
                Header = "Wersja",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            CheckBox offlineConfirmation = new()
            {
                IsEnabled = !hardBlocked,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = "Potwierdzam użycie wyłącznie offline lub "
                        + "w trybie single-player. Rozumiem, że modyfikacje "
                        + "DLL mogą uruchomić ochronę anti-cheat.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            CheckBox experimentalConfirmation = new()
            {
                IsEnabled = !hardBlocked,
                Visibility = Visibility.Collapsed,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = "Rozumiem, że wersje Beta/Nightly są "
                        + "eksperymentalne i mogą powodować awarie gry.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            // Probing the driver store touches the file system and verifies
            // signatures, so keep it off the UI thread.
            NvidiaDriverStoreSnapshot driverStore = await Task.Run(
                _driverStoreProbe.Probe,
                _lifetime.Token);
            OptiScalerSafetyDecision neuralDecision =
                NeuralRenderingPolicy.Evaluate(
                    new(true, OptiScalerSafetyBlockReason.None),
                    driverStore.Capability);
            // Brak modeli w sterowniku nie przesadza sprawy — uzytkownik moze
            // je wskazac. Karta i wersja sterownika juz tak, wiec te dwie
            // bramki pytamy osobno.
            bool neuralHardwareSupported = NeuralRenderingPolicy.Evaluate(
                    new(true, OptiScalerSafetyBlockReason.None),
                    driverStore.Capability with
                    {
                        NeuralRenderingModelAvailable = true,
                    })
                .CanInstall;
            CheckBox neuralRendering = new()
            {
                IsEnabled = !hardBlocked && neuralDecision.CanInstall,
                IsChecked = neuralDecision.CanInstall,
                Visibility = Visibility.Collapsed,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = "Włącz DLSS Neural Rendering. GameShift skopiuje "
                        + "biblioteki DLSS ze sterownika NVIDIA — a te, "
                        + "których sterownik nie ma, weźmie z plików "
                        + "wskazanych poniżej — i ustawi DLSS jako upscaler.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            // Czesc gier nie uruchomi OptiScalera bez dodatkowego skladnika.
            // Pytamy o zgode z gory, zamiast odbijac uzytkownika bledem
            // dopiero w polowie instalacji.
            OptiScalerGameRequirement? gameRequirement =
                OptiScalerGameRequirements.Find(
                    preflight.TargetExecutablePath);
            bool companionMissing =
                gameRequirement is { CompanionAutoInstallable: true }
                && !OptiScalerGameRequirements.IsSatisfied(
                    gameRequirement,
                    EnumerateGameDirectoryFiles(
                        preflight.TargetExecutablePath));
            CheckBox companionInstall = new()
            {
                IsEnabled = !hardBlocked,
                IsChecked = companionMissing,
                Visibility = companionMissing
                    ? Visibility.Visible
                    : Visibility.Collapsed,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = gameRequirement is null
                        ? string.Empty
                        : $"Pobierz i zainstaluj {gameRequirement.CompanionName}"
                            + $" jako {gameRequirement.CompanionFileName}. "
                            + "Ta gra bez niego nie uruchomi OptiScalera. "
                            + "Jeśli w katalogu gry leży już drugi loader "
                            + "(dinput8.dll), GameShift odłoży go na bok i "
                            + "przywróci przy deinstalacji — dwa loadery na "
                            + "raz to właśnie crash na starcie. Projekt "
                            + "otwarty na licencji MIT; instalowane jest "
                            + "jedno przypięte wydanie ze sprawdzaną sumą "
                            + "kontrolną.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            CheckBox agilitySdkUpgrade = new()
            {
                IsEnabled = !hardBlocked,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = "Zaktualizuj DirectX 12 Agility SDK. Część gier bez "
                        + "tego nie uruchomi OptiScalera; pliki są w pakiecie.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            // Neural Rendering potrzebuje dwoch bibliotek NGX: samego modelu
            // i runtime'u DLSS, na ktorym on jezdzi. Sterownik 616.x nie
            // niesie zadnej z nich, wiec kazda dostaje wlasny przycisk —
            // pokazywany tylko wtedy, gdy w magazynie sterownikow jej nie ma.
            NeuralModelPick[] modelPicks =
            [
                new(NvidiaDriverStoreProbe.NeuralRenderingModelFileName),
                new(NvidiaDriverStoreProbe.UpscalerModelFileName),
            ];
            foreach (NeuralModelPick pick in modelPicks)
            {
                pick.Button.IsEnabled = !hardBlocked;
            }

            CheckBox unverifiedModelConfirmation = new()
            {
                Visibility = Visibility.Collapsed,
                Content = new TextBlock
                {
                    MaxWidth = 500,
                    Text = "Użyj wskazanych plików mimo nieudanej "
                        + "weryfikacji. Rozumiem, że Windows nie potwierdził, "
                        + "iż to niezmieniony kod NVIDII, i że zostaną one "
                        + "załadowane do gry z pełnymi prawami procesu gry.",
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            TextBlock neuralStatus = new()
            {
                Foreground = (Brush)Application.Current.Resources[
                    "GameShiftMutedTextBrush"],
                MaxWidth = 500,
                Text = DescribeNeuralRenderingCapability(
                    driverStore,
                    neuralDecision),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
            InfoBar safetyNotice = new()
            {
                IsClosable = false,
                IsOpen = true,
                Severity = hardBlocked
                    ? InfoBarSeverity.Error
                    : InfoBarSeverity.Warning,
                Title = hardBlocked
                    ? "Instalacja zablokowana"
                    : "Tylko gry offline",
                Message = DescribeOptiScalerPreflight(preflight),
            };
            StackPanel content = new()
            {
                MaxWidth = 540,
                Spacing = 12,
            };
            content.Children.Add(new TextBlock
            {
                Text = status.IsInstalled
                    ? $"Zainstalowano OptiScaler {status.Version} "
                        + $"({GetOptiScalerChannelLabel(status.Channel)})."
                    : "GameShift pobierze wybrane wydanie, sprawdzi SHA-256 "
                        + "i umieści pliki obok właściwego EXE gry.",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Foreground = (Brush)Application.Current.Resources[
                    "GameShiftMutedTextBrush"],
                Text = $"Cel: {preflight.TargetExecutablePath}",
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(proxySelector);
            content.Children.Add(channelSelector);
            content.Children.Add(versionSelector);
            content.Children.Add(safetyNotice);
            content.Children.Add(offlineConfirmation);
            content.Children.Add(experimentalConfirmation);
            content.Children.Add(companionInstall);
            content.Children.Add(agilitySdkUpgrade);
            content.Children.Add(neuralRendering);
            content.Children.Add(neuralStatus);
            foreach (NeuralModelPick pick in modelPicks)
            {
                content.Children.Add(pick.Button);
                content.Children.Add(pick.Status);
            }

            content.Children.Add(unverifiedModelConfirmation);

            ContentDialog dialog = new()
            {
                XamlRoot = RootLayout.XamlRoot,
                Title = $"OptiScaler — {selected.DisplayName}",
                Content = content,
                PrimaryButtonText = status.IsInstalled
                    ? "Zaktualizuj"
                    : "Zainstaluj",
                SecondaryButtonText = status.IsInstalled ? "Usuń" : string.Empty,
                CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = false,
            };

            void UpdatePrimaryButton()
            {
                OptiScalerReleaseChannel channel =
                    GetSelectedOptiScalerChannel(channelSelector);
                dialog.IsPrimaryButtonEnabled = !hardBlocked
                    && offlineConfirmation.IsChecked == true
                    && versionSelector.SelectedItem is not null
                    && (!OptiScalerReleaseChannelPolicy.IsExperimental(channel)
                        || experimentalConfirmation.IsChecked == true);
            }

            void UpdateSelectedChannel()
            {
                OptiScalerReleaseChannel channel =
                    GetSelectedOptiScalerChannel(channelSelector);
                OptiScalerReleaseOption[] channelReleases = availableReleases
                    .Where(release => release.Channel == channel)
                    .ToArray();
                versionSelector.Items.Clear();
                foreach (OptiScalerReleaseOption release in channelReleases)
                {
                    ComboBoxItem item = new()
                    {
                        Content = release.PublishedAtUtc is DateTimeOffset published
                            ? $"{release.Version}  •  {published:yyyy-MM-dd}"
                            : release.Version,
                        Tag = release,
                    };
                    versionSelector.Items.Add(item);
                    if (channel == status.Channel
                        && string.Equals(
                            release.Version,
                            status.Version,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        versionSelector.SelectedItem = item;
                    }
                }

                if (versionSelector.SelectedItem is null
                    && versionSelector.Items.Count > 0)
                {
                    versionSelector.SelectedIndex = 0;
                }

                bool isExperimental =
                    OptiScalerReleaseChannelPolicy.IsExperimental(channel);
                experimentalConfirmation.Visibility = isExperimental
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                Visibility neuralVisibility =
                    channel == OptiScalerReleaseChannel.DlssNeuralRendering
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                neuralRendering.Visibility = neuralVisibility;
                neuralStatus.Visibility = neuralVisibility;
                RefreshNeuralModelState();
                safetyNotice.Message = channelReleases.Length == 0
                    ? "Nie udało się pobrać listy wersji dla tego kanału."
                    : DescribeOptiScalerChannel(channel, preflight);
                UpdatePrimaryButton();
            }

            void RefreshNeuralModelState()
            {
                bool neuralChannel =
                    GetSelectedOptiScalerChannel(channelSelector)
                        == OptiScalerReleaseChannel.DlssNeuralRendering;
                bool consented = unverifiedModelConfirmation.IsChecked == true;
                bool consentOffered = false;
                bool everyModelReady = true;
                foreach (NeuralModelPick pick in modelPicks)
                {
                    // Plik ze sterownika bierzemy zawsze, gdy tam jest: pisze
                    // tam tylko TrustedInstaller. O reszte pytamy uzytkownika.
                    bool fromDriver = driverStore.Find(pick.FileName) is not null;
                    bool needed = neuralChannel && !fromDriver;
                    pick.Button.Visibility = needed
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    pick.Status.Visibility = pick.Button.Visibility;
                    if (!needed)
                    {
                        continue;
                    }

                    consentOffered |= pick.NeedsConsent;
                    everyModelReady &=
                        pick.Accepted || (pick.NeedsConsent && consented);
                }

                unverifiedModelConfirmation.Visibility =
                    neuralChannel && consentOffered
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                neuralRendering.IsEnabled = !hardBlocked
                    && neuralHardwareSupported
                    && everyModelReady;
                neuralRendering.IsChecked = neuralRendering.IsEnabled;
            }

            async Task PickNeuralModelAsync(NeuralModelPick pick)
            {
                FileOpenPicker modelPicker = new();
                modelPicker.FileTypeFilter.Add(".dll");
                modelPicker.SuggestedStartLocation =
                    PickerLocationId.ComputerFolder;
                WinRT.Interop.InitializeWithWindow.Initialize(
                    modelPicker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));

                StorageFile? modelFile =
                    await modelPicker.PickSingleFileAsync();
                if (modelFile is null || _lifetime.IsCancellationRequested)
                {
                    return;
                }

                // Weryfikacja czyta podpis z dysku, wiec nie na watku UI.
                NeuralRenderingModelImportResult import = await Task.Run(
                    () => new NeuralRenderingModelImporter().Inspect(
                        modelFile.Path),
                    _lifetime.Token);
                if (!string.Equals(
                    import.FileName,
                    pick.FileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Obie biblioteki przechodza przez ten sam importer, wiec
                    // plik moze byc poprawny i trafic pod zly przycisk.
                    pick.SourcePath = null;
                    pick.Accepted = false;
                    pick.NeedsConsent = false;
                    pick.Status.Text = import.FileName is null
                        ? $"{pick.FileName}: odrzucono. {import.Message}"
                        : $"{pick.FileName}: odrzucono. Ten przycisk czeka na "
                            + $"{pick.FileName}, a wskazany plik to "
                            + $"{import.FileName}.";
                }
                else
                {
                    pick.SourcePath = import.Accepted || import.IsOverridable
                        ? import.SourcePath
                        : null;
                    pick.Accepted = import.Accepted;
                    pick.NeedsConsent = !import.Accepted && import.IsOverridable;
                    pick.Status.Text = import.Accepted
                        ? $"{pick.FileName}: przyjęto {import.SourcePath}. "
                            + import.Message
                        : $"{pick.FileName}: odrzucono. {import.Message}";
                }

                unverifiedModelConfirmation.IsChecked = false;
                RefreshNeuralModelState();
            }

            unverifiedModelConfirmation.Checked += (_, _) =>
                RefreshNeuralModelState();
            unverifiedModelConfirmation.Unchecked += (_, _) =>
                RefreshNeuralModelState();

            foreach (NeuralModelPick pick in modelPicks)
            {
                pick.Button.Click += async (_, _) =>
                    await PickNeuralModelAsync(pick);
            }

            channelSelector.SelectionChanged += (_, _) =>
                UpdateSelectedChannel();
            versionSelector.SelectionChanged += (_, _) =>
                UpdatePrimaryButton();
            offlineConfirmation.Checked += (_, _) => UpdatePrimaryButton();
            offlineConfirmation.Unchecked += (_, _) => UpdatePrimaryButton();
            experimentalConfirmation.Checked += (_, _) =>
                UpdatePrimaryButton();
            experimentalConfirmation.Unchecked += (_, _) =>
                UpdatePrimaryButton();
            UpdateSelectedChannel();

            ContentDialogResult choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.None
                || _lifetime.IsCancellationRequested)
            {
                return;
            }

            // SetBusy, nie samo _isBusy: instalacja ma wlasne okno postepu,
            // ale deinstalacja nie ma zadnego, a sprawdza sumy kontrolne
            // wszystkich zainstalowanych plikow i przywraca kopie zapasowe.
            // Przy samej fladze przyciski wygladaja na aktywne i po cichu
            // ignoruja klikniecia, co czyta sie jako zawieszony program.
            SetBusy(true);
            OptiScalerOperationResult operation;
            try
            {
                if (choice == ContentDialogResult.Secondary)
                {
                    operation = await _optiScaler.RemoveAsync(
                        profileId,
                        _lifetime.Token);
                }
                else
                {
                    OptiScalerProxy proxy =
                        proxySelector.SelectedItem is ComboBoxItem
                        {
                            Tag: OptiScalerProxy selectedProxy,
                        }
                            ? selectedProxy
                            : OptiScalerProxy.Dxgi;
                    OptiScalerReleaseChannel channel =
                        GetSelectedOptiScalerChannel(channelSelector);
                    string? version =
                        versionSelector.SelectedItem is ComboBoxItem
                        {
                            Tag: OptiScalerReleaseOption selectedRelease,
                        }
                            ? selectedRelease.Version
                            : null;
                    operation = await RunOptiScalerInstallWithProgressAsync(
                        RootLayout.XamlRoot,
                        selected.DisplayName,
                        preflightRequest with
                        {
                            Proxy = proxy,
                            Channel = channel,
                            Version = version,
                            OfflineUseConfirmed =
                                offlineConfirmation.IsChecked == true,
                            ExperimentalUseConfirmed =
                                experimentalConfirmation.IsChecked == true,
                            EnableNeuralRendering =
                                channel
                                    == OptiScalerReleaseChannel
                                        .DlssNeuralRendering
                                && neuralRendering.IsChecked == true,
                            UpgradeAgilitySdk =
                                agilitySdkUpgrade.IsChecked == true,
                            InstallRequiredCompanion =
                                companionInstall.IsChecked == true,
                            NeuralRenderingModelPaths =
                            [
                                .. modelPicks
                                    .Select(pick => pick.SourcePath)
                                    .OfType<string>(),
                            ],
                            UnverifiedNeuralModelAccepted =
                                unverifiedModelConfirmation.IsChecked == true,
                        });
                }
            }
            finally
            {
                SetBusy(false);
            }

            await ShowOptiScalerResultAsync(
                RootLayout.XamlRoot,
                selected.DisplayName,
                operation);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            await ShowOptiScalerResultAsync(
                RootLayout.XamlRoot,
                selected.DisplayName,
                new(false, exception.Message));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private static readonly (OptiScalerInstallStage Stage, string Label)[]
        OptiScalerStages =
        [
            (OptiScalerInstallStage.Preparing, "Kontrola bezpieczeństwa"),
            (OptiScalerInstallStage.CheckingHardware, "Sprawdzenie sprzętu"),
            (OptiScalerInstallStage.Downloading, "Pobieranie pakietu"),
            (OptiScalerInstallStage.Extracting, "Rozpakowanie"),
            (OptiScalerInstallStage.ConfiguringNeuralRendering,
                "Konfiguracja Neural Rendering"),
            (OptiScalerInstallStage.CollectingDriverFiles,
                "Pliki DLSS ze sterownika"),
            (OptiScalerInstallStage.BackingUp, "Kopia zapasowa gry"),
            (OptiScalerInstallStage.CopyingFiles, "Instalacja plików"),
            (OptiScalerInstallStage.Completed, "Zakończono"),
        ];

    /// <summary>
    /// Runs an OptiScaler installation behind a dialog that names the stage in
    /// progress. Without it the window sits silent through a package download
    /// and a copy of well over a hundred megabytes.
    /// </summary>
    private async Task<OptiScalerOperationResult>
        RunOptiScalerInstallWithProgressAsync(
            XamlRoot xamlRoot,
            string gameDisplayName,
            OptiScalerInstallRequest request)
    {
        using CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);

        Brush mutedBrush = (Brush)Application.Current.Resources[
            "GameShiftMutedTextBrush"];
        ProgressBar progressBar = new()
        {
            CornerRadius = new CornerRadius(3),
            Foreground = (Brush)Application.Current.Resources[
                "GameShiftAccentBrush"],
            Height = 6,
            IsIndeterminate = true,
            Maximum = 100,
            Minimum = 0,
        };
        TextBlock messageText = new()
        {
            MaxWidth = 460,
            Text = "Przygotowywanie…",
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel stageList = new() { Spacing = 4 };
        Dictionary<OptiScalerInstallStage, TextBlock> stageRows = [];
        foreach ((OptiScalerInstallStage stage, string label) in OptiScalerStages)
        {
            TextBlock row = new()
            {
                Foreground = mutedBrush,
                Text = $"○  {label}",
            };
            stageRows[stage] = row;
            stageList.Children.Add(row);
        }

        StackPanel content = new() { MaxWidth = 500, Spacing = 12 };
        content.Children.Add(messageText);
        content.Children.Add(progressBar);
        content.Children.Add(stageList);

        ContentDialog dialog = new()
        {
            CloseButtonText = "Anuluj",
            Content = content,
            DefaultButton = ContentDialogButton.None,
            Title = $"Instalowanie OptiScaler — {gameDisplayName}",
            XamlRoot = xamlRoot,
        };
        dialog.CloseButtonClick += (_, _) => cancellation.Cancel();

        int reached = 0;
        Progress<OptiScalerInstallProgress> progress = new(update =>
        {
            messageText.Text = update.Message;
            if (update.Percent is double percent)
            {
                progressBar.IsIndeterminate = false;
                progressBar.Value = percent;
            }
            else
            {
                progressBar.IsIndeterminate = true;
            }

            reached = Math.Max(reached, (int)update.Stage);
            foreach ((OptiScalerInstallStage stage, string label) in
                     OptiScalerStages)
            {
                TextBlock row = stageRows[stage];
                int index = (int)stage;
                if (index < reached)
                {
                    row.Text = $"●  {label}";
                    row.Foreground = mutedBrush;
                }
                else if (index == reached)
                {
                    row.Text = $"▸  {label}";
                    row.ClearValue(TextBlock.ForegroundProperty);
                }
                else
                {
                    row.Text = $"○  {label}";
                    row.Foreground = mutedBrush;
                }
            }
        });

        Task<OptiScalerOperationResult> install =
            _optiScaler.InstallAsync(request, progress, cancellation.Token)
                .AsTask();
        _ = dialog.ShowAsync();
        try
        {
            return await install;
        }
        catch (OperationCanceledException)
        {
            return new(
                false,
                "Instalacja została anulowana. Katalog gry wrócił do stanu "
                    + "sprzed zmiany.");
        }
        finally
        {
            dialog.Hide();
        }
    }

    private static ComboBox BuildOptiScalerProxySelector(
        OptiScalerProxy selectedProxy)
    {
        ComboBox selector = new()
        {
            Header = "Proxy DLL",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach ((OptiScalerProxy proxy, string label) in new[]
                 {
                     (OptiScalerProxy.Dxgi, "dxgi.dll — zalecany"),
                     (OptiScalerProxy.Winmm, "winmm.dll"),
                     (OptiScalerProxy.Version, "version.dll"),
                     (OptiScalerProxy.D3d12, "d3d12.dll"),
                 })
        {
            ComboBoxItem item = new()
            {
                Content = label,
                Tag = proxy,
            };
            selector.Items.Add(item);
            if (proxy == selectedProxy)
            {
                selector.SelectedItem = item;
            }
        }

        return selector;
    }

    private static ComboBox BuildOptiScalerChannelSelector(
        OptiScalerReleaseChannel selectedChannel)
    {
        ComboBox selector = new()
        {
            Header = "Kanał wersji",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach ((OptiScalerReleaseChannel channel, string label) in new[]
                 {
                     (OptiScalerReleaseChannel.Stable, "Stabilny — zalecany"),
                     (OptiScalerReleaseChannel.Beta, "Beta — społecznościowy"),
                     (OptiScalerReleaseChannel.Nightly,
                         "Nightly — oficjalny codzienny"),
                     (OptiScalerReleaseChannel.DlssNeuralRendering,
                         "DLSS 5 Neural Rendering — fork"),
                 })
        {
            ComboBoxItem item = new()
            {
                Content = label,
                Tag = channel,
            };
            selector.Items.Add(item);
            if (channel == selectedChannel)
            {
                selector.SelectedItem = item;
            }
        }

        return selector;
    }

    private static OptiScalerReleaseChannel GetSelectedOptiScalerChannel(
        ComboBox selector) =>
        selector.SelectedItem is ComboBoxItem
        {
            Tag: OptiScalerReleaseChannel channel,
        }
            ? channel
            : OptiScalerReleaseChannel.Stable;

    private async Task<IReadOnlyList<OptiScalerReleaseOption>>
        LoadOptiScalerVersionsAsync(
            OptiScalerReleaseChannel channel,
            CancellationToken cancellationToken)
    {
        try
        {
            return await _optiScaler.GetAvailableVersionsAsync(
                channel,
                cancellationToken);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            return [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    private static string GetOptiScalerChannelLabel(
        OptiScalerReleaseChannel channel) =>
        channel switch
        {
            OptiScalerReleaseChannel.Beta => "Beta społecznościowa",
            OptiScalerReleaseChannel.Nightly => "Nightly oficjalny",
            OptiScalerReleaseChannel.DlssNeuralRendering =>
                "DLSS 5 Neural Rendering",
            _ => "kanał stabilny",
        };

    private static IReadOnlyList<string> EnumerateGameDirectoryFiles(
        string targetExecutablePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(targetExecutablePath);
            return directory is null
                ? []
                : [.. Directory.EnumerateFiles(directory, "*.dll")
                    .Select(Path.GetFileName)
                    .OfType<string>()];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string DescribeOptiScalerPreflight(
        OptiScalerInstallPreflight preflight) =>
        preflight.Safety.BlockReason switch
        {
            OptiScalerSafetyBlockReason.GameRunning =>
                "Najpierw zamknij grę.",
            OptiScalerSafetyBlockReason.AntiCheatDetected =>
                "Wykryto grę online albo ochronę anti-cheat: "
                    + preflight.Safety.Evidence
                    + ". GameShift nie wstrzyknie do niej OptiScaler.",
            OptiScalerSafetyBlockReason.RequiredCompanionMissing
                or OptiScalerSafetyBlockReason.ProxyNotSupportedByGame =>
                "Ta gra ma własne wymagania OptiScalera — szczegóły pojawią "
                    + "się po uruchomieniu instalacji.",
            _ => "Nie używaj OptiScaler w grach online. Błędny proxy DLL "
                + "może uniemożliwić start gry; GameShift zachowa kopię "
                + "kolidującego pliku i pozwoli przywrócić stan.",
        };

    /// <summary>
    /// One "wskaż plik" row in the OptiScaler dialog: the button, the line
    /// underneath it, and what the last inspection decided about the file the
    /// user picked. Neural Rendering needs two NGX libraries and a 616.x
    /// driver carries neither, so each gets its own row.
    /// </summary>
    private sealed class NeuralModelPick
    {
        public NeuralModelPick(string fileName)
        {
            FileName = fileName;
            Button = new()
            {
                Content = $"Wskaż {fileName}…",
                Visibility = Visibility.Collapsed,
            };
            Status = new()
            {
                Foreground = (Brush)Application.Current.Resources[
                    "GameShiftMutedTextBrush"],
                MaxWidth = 500,
                Text = $"{fileName}: nie wybrano pliku.",
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed,
            };
        }

        public string FileName { get; }

        public Button Button { get; }

        public TextBlock Status { get; }

        /// <summary>
        /// Set even for a file that failed verification: enabling it takes the
        /// separate, deliberate consent below. Every copy of these libraries in
        /// public circulation is modified, so without that door the feature
        /// does not work at all.
        /// </summary>
        public string? SourcePath { get; set; }

        public bool Accepted { get; set; }

        public bool NeedsConsent { get; set; }
    }

    private static string DescribeNeuralRenderingCapability(
        NvidiaDriverStoreSnapshot driverStore,
        OptiScalerSafetyDecision decision)
    {
        string[] missing = [.. NeuralRenderingModelImporter.AcceptedFileNames
            .Where(name => driverStore.Find(name) is null)];
        string header = $"Wykryto {driverStore.Capability.Generation}, "
            + $"sterownik {driverStore.Capability.DriverVersion}. ";
        if (decision.CanInstall)
        {
            return missing.Length == 0
                ? header + "Obie biblioteki DLSS są w magazynie sterowników."
                : header
                    + "Model nvngx_dlssnr.dll jest w magazynie sterowników, "
                    + $"ale {string.Join(" i ", missing)} już nie — wskaż go "
                    + "poniżej.";
        }

        return decision.BlockReason switch
        {
            OptiScalerSafetyBlockReason.GpuNotSupported =>
                "Neural Rendering wymaga karty GeForce RTX 50 lub nowszej. "
                    + decision.Evidence,
            OptiScalerSafetyBlockReason.DriverTooOld =>
                $"Wymagany sterownik NVIDIA "
                    + $"{NeuralRenderingPolicy.MinimumDriverVersion} lub "
                    + $"nowszy. {decision.Evidence}",
            OptiScalerSafetyBlockReason.NeuralRenderingModelMissing =>
                $"Na tym komputerze brakuje: {string.Join(", ", missing)}. "
                    + "nvngx_dlssnr.dll to sam model Neural Rendering, "
                    + "a nvngx_dlss.dll to runtime DLSS, na którym on "
                    + "jeździ — bez niego przełącznik upscalera nie ma czego "
                    + "załadować. Nie każdy pakiet sterownika je zawiera "
                    + "i nowsza wersja nie musi tego zmienić (sprawdzone na "
                    + "RTX 5070 ze sterownikiem 616.64: pakiet instaluje "
                    + "wyłącznie nvngx_dlssg.dll). Nie ma ich też w paczce "
                    + "OptiScalera ani skąd pobrać legalnie w sposób "
                    + "zautomatyzowany, więc musisz dostarczyć je sam — "
                    + "po jednej kopii na grę.",
            _ => "Neural Rendering jest niedostępny na tym komputerze.",
        };
    }

    private static string DescribeOptiScalerChannel(
        OptiScalerReleaseChannel channel,
        OptiScalerInstallPreflight preflight) =>
        channel switch
        {
            OptiScalerReleaseChannel.Beta =>
                "Beta pochodzi ze społecznościowego, nieoficjalnego repozytorium "
                    + "Optiscaler-Betas. GameShift sprawdzi źródło i SHA-256. "
                    + DescribeOptiScalerPreflight(preflight),
            OptiScalerReleaseChannel.Nightly =>
                "Nightly to oficjalne codzienne wydanie OptiScaler. Może być "
                    + "niestabilne. GameShift sprawdzi źródło i SHA-256. "
                    + DescribeOptiScalerPreflight(preflight),
            OptiScalerReleaseChannel.DlssNeuralRendering =>
                "Fork społecznościowy z DLSS 5 Neural Rendering. GameShift "
                    + "instaluje wyłącznie jedno, przypięte i zweryfikowane "
                    + "wydanie; binarki nie da się odtworzyć z kodu, więc "
                    + "opiera się to na zaufaniu do autora. "
                    + DescribeOptiScalerPreflight(preflight),
            _ => DescribeOptiScalerPreflight(preflight),
        };

    private static async Task ShowOptiScalerResultAsync(
        XamlRoot xamlRoot,
        string gameDisplayName,
        OptiScalerOperationResult result)
    {
        ContentDialog resultDialog = new()
        {
            XamlRoot = xamlRoot,
            Title = result.Succeeded
                ? $"OptiScaler — {gameDisplayName}"
                : "Nie wykonano operacji OptiScaler",
            Content = new TextBlock
            {
                MaxWidth = 520,
                Text = result.Message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        };
        _ = await resultDialog.ShowAsync();
    }

    private async Task LaunchProfileThroughGameShiftAsync(
        ProfileListItem selected,
        bool keepWindowHidden,
        CancellationToken cancellationToken)
    {
        ProfilesList.SelectedItem = selected;
        if (_activeSession is not null)
        {
            if (!keepWindowHidden)
            {
                NavigateToPage("plan");
            }

            string activeMessage = _activeSession.ProfileId
                == selected.Profile.ProfileId.Value
                    ? $"„{selected.DisplayName}” jest już uruchomiona "
                        + "i monitorowana przez GameShift."
                    : $"Najpierw zakończ aktywną sesję "
                        + $"„{_activeSession.GameDisplayName}”.";
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Tryb gry jest już aktywny",
                activeMessage);
            _trayIcon?.ShowNotification(
                "GameShift — aktywna sesja",
                activeMessage);
            return;
        }

        try
        {
            SetBusy(true);
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Informational,
                $"Uruchamianie: {selected.DisplayName}",
                "GameShift ponownie weryfikuje plik EXE i przygotowuje "
                + "bezpieczną sesję.");

            SessionPlanClientSnapshot plan =
                await _sessions.PrepareAsync(
                    selected.Profile.ProfileId.Value,
                    backgroundApplications: [],
                    GamePriorityClientMode.Normal,
                    cancellationToken,
                    useSavedBackgroundRules: true);
            _sessionEndpointAvailable = true;
            _pendingPlan = plan;
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Informational,
                "Gra startuje",
                "Po uruchomieniu GameShift zastosuje zatwierdzone działania "
                + "w tle, kolejno i z pełnym recovery.");

            SessionStateClientSnapshot active =
                await _sessions.StartAsync(
                    plan.PlanId,
                    plan.SessionId,
                    cancellationToken,
                    enableFrameRateTracking: FpsTrackingToggleSwitch.IsOn,
                enableProBalance: ProBalanceToggleSwitch.IsOn);
            _pendingPlan = null;
            _planActions.Clear();
            _activeSession = active;
            ApplyActiveSession(active);
            if (keepWindowHidden)
            {
                HideToTray();
                _trayIcon?.ShowNotification(
                    "GameShift — gra uruchomiona",
                    $"„{active.GameDisplayName}” działa w Trybie gry. "
                    + "GameShift pozostaje w zasobniku systemowym.");
            }
            else
            {
                NavigateToPage("plan");
            }

            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Success,
                "Gra uruchomiona",
                active.Message);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            if (IsSessionConnectionFailure(exception))
            {
                _sessionEndpointAvailable = false;
                ApplySessionUnavailable(exception.Message);
            }

            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Error,
                "Nie uruchomiono gry",
                exception.Message);
            _trayIcon?.ShowNotification(
                "GameShift — nie uruchomiono gry",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPreparePlanClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        if (ProfilesList.SelectedItem is not ProfileListItem selected)
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Brak profilu",
                "Wybierz aktywny profil gry przed przygotowaniem planu.");
            return;
        }

        GamePriorityClientMode selectedGamePriority =
            GetSelectedGamePriority();
        if (selectedGamePriority != GamePriorityClientMode.Normal
            && !await ConfirmExperimentalGamePriorityAsync(
                    selectedGamePriority))
        {
            GamePrioritySelector.SelectedIndex = 0;
            return;
        }

        try
        {
            SetBusy(true);
            if (RememberRulesCheckBox.IsChecked == true)
            {
                _loadedPreferences =
                    await SaveSelectedOptimizationPreferencesAsync(
                        selected.Profile.ProfileId,
                        _lifetime.Token);
            }

            SessionPlanClientSnapshot plan =
                await _sessions.PrepareAsync(
                    selected.Profile.ProfileId.Value,
                    _backgroundApplications
                        .Where(application =>
                            application.IsSelected)
                        .Select(application =>
                            application.ToSelection())
                        .ToArray(),
                    selectedGamePriority,
                    _lifetime.Token);
            _sessionEndpointAvailable = true;
            _pendingPlan = plan;
            ApplyPreparedPlan(plan);
            NavigateToPage("plan");
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Success,
                "Plan gotowy do sprawdzenia",
                "Gra nie została jeszcze uruchomiona. "
                + "Przejrzyj działania i osobno zatwierdź start.");
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            if (IsSessionConnectionFailure(exception))
            {
                _sessionEndpointAvailable = false;
                ApplySessionUnavailable(exception.Message);
            }

            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Nie przygotowano planu",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartSessionClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy || _pendingPlan is null)
        {
            return;
        }

        SessionPlanClientSnapshot plan = _pendingPlan;
        if (plan.ExpiresAtUtc < DateTimeOffset.UtcNow)
        {
            ClearPreparedPlan();
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Plan wygasł",
                "Przygotuj nowy plan, aby ponownie zweryfikować plik EXE.");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = $"Włączyć Tryb gry dla „{plan.GameDisplayName}”?",
            Content =
                BuildStartConfirmation(plan),
            PrimaryButtonText = "Zgadzam się i włączam",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            SetBusy(true);
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Informational,
                "Gra startuje",
                "Proces gry jest uruchamiany przed sekwencyjnymi działaniami "
                + "tła. Każda zmiana nadal przechodzi verify i recovery.");
            SessionStateClientSnapshot active =
                await _sessions.StartAsync(
                    plan.PlanId,
                    plan.SessionId,
                    _lifetime.Token,
                    enableFrameRateTracking: FpsTrackingToggleSwitch.IsOn,
                enableProBalance: ProBalanceToggleSwitch.IsOn);
            _sessionEndpointAvailable = true;
            _pendingPlan = null;
            _planActions.Clear();
            _activeSession = active;
            ApplyActiveSession(active);
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Success,
                "Sesja aktywna",
                active.Message);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            if (IsSessionConnectionFailure(exception))
            {
                _sessionEndpointAvailable = false;
                ApplySessionUnavailable(exception.Message);
            }

            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Nie uruchomiono sesji",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRestoreSessionClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy || _activeSession is null)
        {
            return;
        }

        SessionStateClientSnapshot active = _activeSession;
        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Przywrócić aplikacje teraz?",
            Content =
                $"GameShift przywróci aplikacje zamknięte dla "
                + $"„{active.GameDisplayName}” i zakończy Tryb gry. "
                + "Proces gry pozostanie uruchomiony.",
            PrimaryButtonText = "Przywróć aplikacje",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            SetBusy(true);
            SessionStateClientSnapshot completed =
                await _sessions.RestoreAsync(
                    active.SessionId,
                    _lifetime.Token);
            _sessionEndpointAvailable = true;
            if (completed.State == "RecoveryRequired")
            {
                _activeSession = completed;
                ApplyActiveSession(completed);
                ShowInfo(
                    PlanInfoBar,
                    InfoBarSeverity.Error,
                    "Recovery wymaga ponowienia",
                    completed.Message);
                return;
            }

            _activeSession = null;
            ClearPreparedPlan();
            ApplyCompletedSession(completed);
            await RefreshDashboardRecoveryStatusAsync(_lifetime.Token);
            await LoadHistoryAsync(_lifetime.Token);
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Success,
                "Aplikacje przywrócone",
                completed.Message);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            if (IsSessionConnectionFailure(exception))
            {
                _sessionEndpointAvailable = false;
                ApplySessionUnavailable(exception.Message);
            }

            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Nie zakończono sesji",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCloseGameClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy || _activeSession is null)
        {
            return;
        }

        SessionStateClientSnapshot active = _activeSession;
        try
        {
            SetBusy(true);
            SessionStateClientSnapshot result =
                await _sessions.CloseGameAsync(
                    active.SessionId,
                    _lifetime.Token);
            _activeSession = null;
            ApplyCompletedSession(result);
            await RefreshDashboardRecoveryStatusAsync(_lifetime.Token);
            await LoadHistoryAsync(_lifetime.Token);
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Success,
                "Gra zakończona natychmiast",
                result.Message);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Nie zamknięto gry",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSessionPollTick(object? sender, object args)
    {
        if (_isBusy
            || _isSessionPollRunning
            || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isSessionPollRunning = true;
        try
        {
            UpdateDashboardSystemMetrics();
            await RefreshActiveSessionCoreAsync(_lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _isSessionPollRunning = false;
        }
    }

    private async void OnAddProfileClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            FileOpenPicker picker = new();
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            nint windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                windowHandle);

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null || _lifetime.IsCancellationRequested)
            {
                return;
            }

            TextBox displayNameInput = new()
            {
                Header = "Nazwa profilu",
                MaxLength = 120,
                Text = Path.GetFileNameWithoutExtension(file.Path),
            };
            ComboBox presetInput = new()
            {
                Header = "Tryb",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = 0,
            };
            presetInput.Items.Add(
                new ComboBoxItem
                {
                    Content = "Bezpieczny",
                    Tag = OptimizationPreset.Safe,
                });
            presetInput.Items.Add(
                new ComboBoxItem
                {
                    Content = "Zrównoważony",
                    Tag = OptimizationPreset.Balanced,
                });
            TextBox argumentsInput = new()
            {
                AcceptsReturn = true,
                Header = "Argumenty uruchomienia — jeden argument w wierszu",
                MaxHeight = 160,
                MinHeight = 72,
                TextWrapping = TextWrapping.NoWrap,
            };
            StackPanel dialogContent = new()
            {
                Spacing = 12,
            };
            dialogContent.Children.Add(displayNameInput);
            dialogContent.Children.Add(presetInput);
            dialogContent.Children.Add(argumentsInput);

            ContentDialog dialog = new()
            {
                XamlRoot = RootLayout.XamlRoot,
                Title = "Nowy profil gry",
                Content = dialogContent,
                PrimaryButtonText = "Zapisz profil",
                CloseButtonText = "Anuluj",
                DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary
                || _lifetime.IsCancellationRequested)
            {
                return;
            }

            OptimizationPreset preset =
                presetInput.SelectedItem is ComboBoxItem selectedPreset
                    && selectedPreset.Tag is OptimizationPreset selectedValue
                        ? selectedValue
                        : OptimizationPreset.Safe;
            string[] launchArguments = argumentsInput.Text.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries);

            SetBusy(true);
            ManualGameProfile profile =
                await _profileFactory.CreateAsync(
                    displayNameInput.Text,
                    file.Path,
                    launchArguments,
                    preset,
                    _lifetime.Token);
            await _userDataStore.UpsertAsync(profile, _lifetime.Token);
            await LoadProfilesAsync(_lifetime.Token);
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Success,
                "Profil zapisany",
                "Plik EXE został zahashowany i zapisany lokalnie.");
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Error,
                "Nie zapisano profilu",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeleteProfileClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        if (ProfilesList.SelectedItem is not ProfileListItem selected)
        {
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Warning,
                "Brak zaznaczenia",
                "Wybierz profil, który chcesz usunąć.");
            return;
        }

        if (_activeSession?.ProfileId
            == selected.Profile.ProfileId.Value)
        {
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Warning,
                "Profil jest używany",
                "Najpierw zakończ monitorowanie aktywnej sesji. "
                + "GameShift nie zamknie przy tym gry.");
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Usunąć profil?",
            Content =
                $"Profil „{selected.DisplayName}” zostanie usunięty "
                + "z lokalnej bazy. Plik gry nie zostanie zmieniony.",
            PrimaryButtonText = "Usuń profil",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            SetBusy(true);
            bool deleted = await _userDataStore.DeleteAsync(
                selected.Profile.ProfileId,
                _lifetime.Token);
            if (deleted
                && _pendingPlan?.ProfileId
                    == selected.Profile.ProfileId.Value)
            {
                ClearPreparedPlan();
            }

            await LoadProfilesAsync(_lifetime.Token);
            ShowInfo(
                ProfilesInfoBar,
                deleted
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                deleted ? "Profil usunięty" : "Profilu nie znaleziono",
                deleted
                    ? "Usunięto wyłącznie wpis z lokalnej bazy."
                    : "Lista została odświeżona.");
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                ProfilesInfoBar,
                InfoBarSeverity.Error,
                "Nie usunięto profilu",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDeleteOldHistoryClicked(
        object sender,
        RoutedEventArgs args)
    {
        if (_isBusy)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Usunąć starą historię?",
            Content =
                "Lokalne podsumowania zakończone ponad 90 dni temu "
                + "zostaną trwale usunięte.",
            PrimaryButtonText = "Usuń stare wpisy",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            SetBusy(true);
            int deleted = await _userDataStore.DeleteEndedBeforeAsync(
                DateTimeOffset.UtcNow - HistoryRetention,
                _lifetime.Token);
            await LoadHistoryAsync(_lifetime.Token);
            ShowInfo(
                HistoryInfoBar,
                InfoBarSeverity.Success,
                "Retencja zakończona",
                $"Usunięto wpisów: {deleted}.");
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                HistoryInfoBar,
                InfoBarSeverity.Error,
                "Nie usunięto historii",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnThemeSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (ThemeSelector.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ElementTheme selectedTheme = item.Tag?.ToString() switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        RootLayout.RequestedTheme = selectedTheme;
        UpdateTitleBarTheme(
            selectedTheme == ElementTheme.Default
                ? RootLayout.ActualTheme
                : selectedTheme);
    }

    private void UpdateTitleBarTheme(ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        bool isLight = theme == ElementTheme.Light;
        WindowsColor background = isLight
            ? WindowsColor.FromArgb(255, 248, 251, 252)
            : WindowsColor.FromArgb(255, 5, 8, 12);
        WindowsColor foreground = isLight
            ? WindowsColor.FromArgb(255, 22, 31, 45)
            : WindowsColor.FromArgb(255, 238, 244, 255);
        WindowsColor inactiveForeground = isLight
            ? WindowsColor.FromArgb(255, 88, 102, 121)
            : WindowsColor.FromArgb(255, 139, 154, 178);
        WindowsColor transparent =
            WindowsColor.FromArgb(0, 0, 0, 0);

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = isLight
            ? WindowsColor.FromArgb(255, 222, 231, 243)
            : WindowsColor.FromArgb(255, 30, 40, 58);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = isLight
            ? WindowsColor.FromArgb(255, 207, 219, 235)
            : WindowsColor.FromArgb(255, 40, 54, 78);
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private async Task LoadUpdatePreferencesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            UpdatePreferences preferences =
                await _userDataStore.LoadUpdatePreferencesAsync(
                    cancellationToken);
            _updatePreferences = preferences;
            _isLoadingUpdatePreferences = true;
            AutomaticUpdateChecksToggle.IsOn =
                preferences.AutomaticChecksEnabled;
            AutomaticUpdateInstallToggle.IsOn =
                preferences.AutomaticInstallEnabled;
            UpdateLastCheckText(preferences);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            _updatePreferences = UpdatePreferences.CreateDefault();
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Nie wczytano ustawień aktualizacji",
                "GameShift użyje bezpiecznych ustawień domyślnych. "
                + exception.Message);
        }
        finally
        {
            _isLoadingUpdatePreferences = false;
            UpdateUpdateControls();
        }
    }

    private async Task CheckForUpdatesAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        if (_isUpdateOperationRunning)
        {
            return;
        }

        _isUpdateOperationRunning = true;
        UpdateUpdateControls();
        try
        {
            if (manual)
            {
                ShowInfo(
                    UpdateInfoBar,
                    InfoBarSeverity.Informational,
                    "Sprawdzanie aktualizacji",
                    "Pobieram mały, podpisany manifest wydania.");
            }

            UpdateCheckResult result = await _updates.CheckAsync(
                manual,
                cancellationToken);
            _updatePreferences = result.Preferences;
            UpdateLastCheckText(result.Preferences);
            UpdatesStatusText.Text = result.Message;
            if (result.Manifest is null)
            {
                _availableUpdate = null;
                _stagedInstallerPath = null;
            }

            switch (result.State)
            {
                case UpdateCheckState.NotDue:
                    UpdateActivitySpinner.IsActive = false;
                    UpdateActivitySpinner.Visibility = Visibility.Collapsed;
                    return;
                case UpdateCheckState.UpToDate:
                case UpdateCheckState.AheadOfChannel:
                case UpdateCheckState.ManualUpgradeRequired:
                    UpdateActivitySpinner.IsActive = false;
                    UpdateActivitySpinner.Visibility = Visibility.Collapsed;
                    ApplyNoAvailableUpdate();
                    if (manual)
                    {
                        ShowInfo(
                            UpdateInfoBar,
                            result.State == UpdateCheckState.UpToDate
                                ? InfoBarSeverity.Success
                                : InfoBarSeverity.Informational,
                            result.State switch
                            {
                                UpdateCheckState.AheadOfChannel =>
                                    "Wersja nowsza niż kanał",
                                UpdateCheckState.ManualUpgradeRequired =>
                                    "Wymagana ręczna reinstalacja",
                                _ => "GameShift jest aktualny",
                            },
                            result.Message);
                    }

                    break;
                case UpdateCheckState.Available:
                    _availableUpdate = result.Manifest;
                    _stagedInstallerPath = null;
                    SetUpdatePipelineStep(1);
                    ApplyAvailableUpdate(result.Manifest!);
                    ShowInfo(
                        UpdateInfoBar,
                        InfoBarSeverity.Informational,
                        "Dostępna aktualizacja",
                        result.Message);

                    SessionStateClientSnapshot? activeSession =
                        await _sessions.GetActiveAsync(cancellationToken);
                    if (activeSession is null)
                    {
                        ShowInfo(
                            UpdateInfoBar,
                            InfoBarSeverity.Informational,
                            "Pobieranie aktualizacji w tle",
                            $"Pobieram pakiet wersji {result.Manifest!.Version}. Po zakończeniu wystarczy zatwierdzić instalację przyciskiem.");
                        await DownloadAvailableUpdateCoreAsync(cancellationToken);
                    }
                    else
                    {
                        _automaticUpdateDeferredForSession = true;
                        UpdatesStatusText.Text =
                            "Nowa wersja czeka. Pobieranie w tle rozpocznie się po zakończeniu gry.";
                    }

                    break;
                case UpdateCheckState.Failed:
                    UpdateActivitySpinner.IsActive = false;
                    UpdateActivitySpinner.Visibility = Visibility.Collapsed;
                    ShowInfo(
                        UpdateInfoBar,
                        InfoBarSeverity.Warning,
                        "Nie sprawdzono aktualizacji",
                        result.Message);
                    break;
                default:
                    throw new InvalidDataException(
                        "Klient zwrócił nieznany stan aktualizacji.");
            }
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Aktualizacja oczekuje",
                "Pakiet jest gotowy, ale instalacja nie otrzymała zgody "
                + "administratora.");
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Error,
                "Błąd aktualizacji",
                exception.Message);
        }
        finally
        {
            _isUpdateOperationRunning = false;
            UpdateActivitySpinner.IsActive = false;
            UpdateActivitySpinner.Visibility = Visibility.Collapsed;
            UpdateUpdateControls();
        }
    }

    private async Task DownloadAvailableUpdateCoreAsync(
        CancellationToken cancellationToken)
    {
        SignedUpdateManifest manifest = _availableUpdate
            ?? throw new InvalidOperationException(
                "Najpierw sprawdź dostępność aktualizacji.");

        SetUpdatePipelineStep(2);
        UpdateActivitySpinner.IsActive = true;
        UpdateActivitySpinner.Visibility = Visibility.Visible;
        UpdateDownloadProgressBar.Value = 0;
        UpdateDownloadPercentText.Text = "0%";
        StagedUpdateText.Text = "Pobieranie i weryfikowanie fragmentów…";

        Progress<UpdateDownloadProgress> progress = new(update =>
        {
            UpdateDownloadProgressBar.Value = update.Percent;
            UpdateDownloadPercentText.Text = $"{update.Percent:0}%";
            double downloadedMib = update.DownloadedBytes / (1024.0 * 1024.0);
            double totalMib = update.TotalBytes / (1024.0 * 1024.0);
            StagedUpdateText.Text =
                $"Pobieranie: {update.CompletedChunks}/{update.TotalChunks} fragmentów • "
                + $"{downloadedMib:0.0} / {totalMib:0.0} MiB";
        });

        _stagedInstallerPath = await _updates.DownloadInstallerAsync(
            manifest,
            progress,
            cancellationToken);

        SetUpdatePipelineStep(3);
        StagedUpdateText.Text = "Weryfikacja kryptograficzna i sprawdzanie SHA-256…";

        await using (await _updates.VerifyInstallerAsync(
            manifest,
            _stagedInstallerPath,
            cancellationToken))
        {
        }

        SetUpdatePipelineStep(4);
        UpdateActivitySpinner.IsActive = false;
        UpdateActivitySpinner.Visibility = Visibility.Collapsed;
        UpdateDownloadProgressBar.Value = 100;
        UpdateDownloadPercentText.Text = "100%";
        StagedUpdateText.Text =
            $"Pakiet wersji {manifest.Version} został pobrany i zweryfikowany z SHA-256.";
        ShowInfo(
            UpdateInfoBar,
            InfoBarSeverity.Success,
            "Aktualizacja gotowa do instalacji",
            $"Wersja {manifest.Version} została pobrana. Kliknij „Zainstaluj aktualizację teraz”, aby zatwierdzić instalację.");
        _trayIcon?.ShowNotification(
            "GameShift — aktualizacja gotowa",
            $"Nowa wersja {manifest.Version} została pobrana. Kliknij, aby zatwierdzić instalację.");
        TacticalAudioService.Instance.PlaySuccess();
        TacticalVfxService.Instance.AnimatePulse(InstallUpdateButton);
        UpdateUpdateControls();
    }

    private async Task LaunchVerifiedInstallerCoreAsync(
        bool automatic,
        CancellationToken cancellationToken)
    {
        SignedUpdateManifest manifest = _availableUpdate
            ?? throw new InvalidOperationException(
                "Brak podpisanego manifestu aktualizacji.");
        string installerPath = _stagedInstallerPath
            ?? throw new InvalidOperationException(
                "Najpierw pobierz aktualizację.");

        SessionStateClientSnapshot? active =
            await _sessions.GetActiveAsync(cancellationToken);
        if (active is not null)
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Warning,
                "Aktualizacja odłożona",
                $"Najpierw zakończ sesję „{active.GameDisplayName}”. "
                + "GameShift nie zamknie gry dla aktualizacji.");
            return;
        }

        RecoveryJournalInspection inspection =
            await RecoveryJournalInspector.InspectAsync(
                GameShiftStoragePaths.UserRecoveryJournalPath,
                cancellationToken);
        if (!inspection.IsClean)
        {
            ShowInfo(
                UpdateInfoBar,
                InfoBarSeverity.Error,
                "Aktualizacja zablokowana przez recovery",
                "Najpierw przywróć niedokończoną sesję: "
                + string.Join(
                    ", ",
                    inspection.UnfinishedSessionIds.Select(
                        id => id.ToString("D"))));
            return;
        }

        // Uchwyt zyje az do uruchomienia procesu. Miedzy sprawdzeniem sumy
        // a startem nikt nie podmieni ani nie skasuje pliku, ktory zaraz
        // dostanie prawa administratora.
        await using FileStream verified = await _updates.VerifyInstallerAsync(
            manifest,
            installerPath,
            cancellationToken);
        ProcessStartInfo startInfo = new()
        {
            FileName = installerPath,
            WorkingDirectory = Path.GetDirectoryName(installerPath),
            UseShellExecute = true,
            Verb = "runas",
            Arguments = automatic
                ? "/SILENT /NORESTART /GameShiftUpdate=1"
                : "/NORESTART /GameShiftUpdate=1",
        };
        using Process? installer = Process.Start(startInfo);
        if (installer is null)
        {
            throw new InvalidOperationException(
                "Windows nie uruchomił zweryfikowanego instalatora.");
        }

        ShowInfo(
            UpdateInfoBar,
            InfoBarSeverity.Success,
            "Instalator uruchomiony",
            "GameShift zamknie wyłącznie własne składniki po ponownej "
            + "kontroli aktywnej sesji i recovery.");
    }

    private void SetUpdatePipelineStep(int activeStep)
    {
        if (UpdateStep1Badge is null)
        {
            return;
        }

        SolidColorBrush accentBg = new(WindowsColor.FromArgb(40, 0, 215, 226));
        SolidColorBrush accentBorder = new(WindowsColor.FromArgb(255, 0, 215, 226));
        SolidColorBrush successBg = new(WindowsColor.FromArgb(40, 61, 203, 112));
        SolidColorBrush successBorder = new(WindowsColor.FromArgb(255, 61, 203, 112));
        SolidColorBrush idleBg = new(WindowsColor.FromArgb(20, 255, 255, 255));
        SolidColorBrush idleBorder = new(WindowsColor.FromArgb(40, 255, 255, 255));

        UpdateStep1Badge.Background = activeStep >= 1 ? accentBg : idleBg;
        UpdateStep1Badge.BorderBrush = activeStep >= 1 ? accentBorder : idleBorder;

        UpdateStep2Badge.Background = activeStep >= 2 ? accentBg : idleBg;
        UpdateStep2Badge.BorderBrush = activeStep >= 2 ? accentBorder : idleBorder;

        UpdateStep3Badge.Background = activeStep >= 3 ? accentBg : idleBg;
        UpdateStep3Badge.BorderBrush = activeStep >= 3 ? accentBorder : idleBorder;

        UpdateStep4Badge.Background = activeStep >= 4 ? successBg : idleBg;
        UpdateStep4Badge.BorderBrush = activeStep >= 4 ? successBorder : idleBorder;
    }

    private void ApplyAvailableUpdate(SignedUpdateManifest manifest)
    {
        AvailableUpdateVersionText.Text = manifest.DisplayName;
        UpdateReleaseNotesText.Text = manifest.ReleaseNotes.Count == 0
            ? "Wydawca nie dodał informacji o zmianach."
            : string.Join(
                Environment.NewLine,
                manifest.ReleaseNotes.Select(note => $"• {note}"));
        UpdateDownloadProgressBar.Value = 0;
        UpdateDownloadPercentText.Text = "0%";
        StagedUpdateText.Text = "Pakiet gotowy do pobrania.";
        UpdateUpdateControls();
    }

    private void ApplyNoAvailableUpdate()
    {
        AvailableUpdateVersionText.Text = "Brak nowej wersji";
        UpdateReleaseNotesText.Text =
            "Zainstalowana wersja jest zgodna z bieżącym kanałem.";
        UpdateDownloadProgressBar.Value = 0;
        UpdateDownloadPercentText.Text = "0%";
        StagedUpdateText.Text = "Pakiet nie jest potrzebny.";
        SetUpdatePipelineStep(0);
        UpdateUpdateControls();
    }

    private void UpdateLastCheckText(UpdatePreferences preferences)
    {
        UpdatesLastCheckText.Text = preferences.LastSuccessfulCheckAtUtc
            is DateTimeOffset lastCheck
                ? $"Ostatnie sprawdzenie: {lastCheck.ToLocalTime():g}"
                : "Ostatnie sprawdzenie: nigdy";
    }

    private void UpdateUpdateControls()
    {
        if (CheckForUpdatesButton is null)
        {
            return;
        }

        CheckForUpdatesButton.IsEnabled = !_isUpdateOperationRunning;
        AutomaticUpdateChecksToggle.IsEnabled =
            !_isUpdateOperationRunning;
        AutomaticUpdateInstallToggle.IsEnabled =
            !_isUpdateOperationRunning;
        DownloadUpdateButton.IsEnabled =
            !_isUpdateOperationRunning && _availableUpdate is not null;
        InstallUpdateButton.IsEnabled =
            !_isUpdateOperationRunning
            && _availableUpdate is not null
            && !string.IsNullOrWhiteSpace(_stagedInstallerPath);
    }

    private async Task RefreshAllAsync(
        CancellationToken cancellationToken)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _userDataStore.InitializeAsync(cancellationToken);
            await LoadProfilesAsync(cancellationToken);
            await LoadHistoryAsync(cancellationToken);
            await RefreshActiveSessionCoreAsync(cancellationToken);
            await RefreshDashboardRecoveryStatusAsync(cancellationToken);
            await RefreshDiagnosticsCoreAsync(cancellationToken);
            DashboardInfoBar.IsOpen = false;
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Error,
                "Nie udało się odświeżyć danych",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SyncGameLibraryAsync(
        bool showResultWhenUnchanged,
        CancellationToken cancellationToken)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            GameLibrarySyncResult result =
                await _gameLibrarySync.SyncAsync(
                    _userDataStore,
                    cancellationToken);
            await LoadProfilesAsync(cancellationToken);
            LibraryScanText.Text =
                $"Ostatnie wykrywanie: {DateTimeOffset.Now:t} • "
                + $"znaleziono {result.DetectedCount}";
            PlanLibraryStatusText.Text =
                result.DetectedCount == 0
                    ? "Nie znaleziono gry automatycznie. Możesz dodać plik "
                        + "EXE w Bibliotece gier."
                    : $"Biblioteka gotowa • wykryto: "
                        + $"{result.DetectedCount}";

            if (result.AddedCount == 0
                && result.UpdatedCount == 0
                && !showResultWhenUnchanged)
            {
                return;
            }

            InfoBarSeverity severity = result.ErrorCount == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            string title = result.AddedCount > 0
                ? "Biblioteka gier uzupełniona"
                : result.UpdatedCount > 0
                    ? "Profile gier odświeżone"
                    : "Biblioteka jest aktualna";
            string message =
                $"Wykryto: {result.DetectedCount}, dodano: "
                + $"{result.AddedCount}, zaktualizowano: "
                + $"{result.UpdatedCount}, pominięto z błędem: "
                + $"{result.ErrorCount}.";
            ShowInfo(
                ProfilesInfoBar,
                severity,
                title,
                message);
            ShowInfo(
                PlanInfoBar,
                severity,
                title,
                message);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            PlanLibraryStatusText.Text =
                "Automatyczne wykrywanie nie powiodło się.";
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Error,
                "Nie wykryto gier",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await RefreshDiagnosticsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            DiagnosticsSessionHostText.Text =
                $"Błąd diagnostyki: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDiagnosticsCoreAsync(
        CancellationToken cancellationToken)
    {
        Task<DiagnosticsEndpointSnapshot> sessionHostTask =
            _diagnostics.ProbeAsync(
                "SessionHost",
                PipeNames.ForUser(_userSid),
                cancellationToken);
        Task<DiagnosticsEndpointSnapshot> systemAgentTask =
            _diagnostics.ProbeAsync(
                "SystemAgent",
                PipeNames.System,
                cancellationToken);
        DiagnosticsEndpointSnapshot[] endpoints =
            await Task.WhenAll(sessionHostTask, systemAgentTask);

        ApplyEndpointSnapshot(
            endpoints[0],
            SessionHostStateText,
            SessionHostDetailsText,
            DiagnosticsSessionHostText);
        ApplyDashboardEndpointIndicator(
            endpoints[0],
            DashboardSessionHostStatusIndicator,
            DashboardSessionHostStatusGlyph);
        ApplyEndpointSnapshot(
            endpoints[1],
            SystemAgentStateText,
            SystemAgentDetailsText,
            DiagnosticsSystemAgentText);
        ApplyDashboardEndpointIndicator(
            endpoints[1],
            DashboardSystemAgentStatusIndicator,
            DashboardSystemAgentStatusGlyph);
    }

    /// <summary>
    /// Reads the Memory Optimizer's state. It is a separate process talking
    /// over IPC, so "not running" is a normal answer and must not be able to
    /// take the window down with it — this runs on the startup path, inside an
    /// async void handler, where an escaping exception ends the process before
    /// the user sees anything at all.
    /// </summary>
    private async Task RefreshMemoryOptimizerStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            MemoryOptimizerComponentSnapshot snapshot =
                await _memoryOptimizer.GetStatusAsync(cancellationToken);
            MemoryOptimizerStatusText.Text = snapshot.DisplayState;
            MemoryOptimizerDetailsText.Text = snapshot.Details;
            OpenMemoryOptimizerButton.IsEnabled = snapshot.CanOpen;
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            MemoryOptimizerStatusText.Text = "Niedostępny";
            MemoryOptimizerDetailsText.Text =
                "Nie udało się odczytać stanu: " + exception.Message;
            OpenMemoryOptimizerButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Same story as the Memory Optimizer above: a Windows service that may
    /// simply not be installed. Its absence is information to show, not a
    /// reason to fail to start.
    /// </summary>
    private async Task RefreshSystemOptimizerStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            SystemOptimizerComponentSnapshot snapshot =
                await _systemOptimizer.GetStatusAsync(cancellationToken);
            SystemOptimizerStatusText.Text = snapshot.DisplayState;
            SystemOptimizerProfileText.Text = snapshot.Details;
            OpenSystemOptimizerButton.IsEnabled = snapshot.CanOpen;
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            SystemOptimizerStatusText.Text = "Niedostępny";
            SystemOptimizerProfileText.Text =
                "Nie udało się odczytać stanu: " + exception.Message;
            OpenSystemOptimizerButton.IsEnabled = false;
        }
    }

    private async Task RefreshDashboardRecoveryStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            RecoveryJournalInspection inspection =
                await RecoveryJournalInspector.InspectAsync(
                    GameShiftStoragePaths.UserRecoveryJournalPath,
                    cancellationToken);
            _isRecoveryJournalClean = inspection.IsClean;
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            _isRecoveryJournalClean = null;
        }

        UpdateDashboardRecoveryState(_activeSession);
    }

    private async Task RefreshActiveSessionCoreAsync(
        CancellationToken cancellationToken)
    {
        SessionStateClientSnapshot? previous = _activeSession;
        SessionStateClientSnapshot? current;
        try
        {
            current = await _sessions.GetActiveAsync(cancellationToken);
            _sessionEndpointAvailable = true;
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            _sessionEndpointAvailable = false;
            ApplySessionUnavailable(exception.Message);
            UpdateSessionControls();
            return;
        }

        _activeSession = current;
        if (current is not null)
        {
            _pendingPlan = null;
            _planActions.Clear();
            _detectedRunningUnoptimizedGame = null;
            if (UnoptimizedGameBanner is not null)
            {
                UnoptimizedGameBanner.Visibility = Visibility.Collapsed;
            }

            ApplyActiveSession(current);
        }
        else
        {
            ApplyNoActiveSession();
            if (previous is not null)
            {
                await RefreshDashboardRecoveryStatusAsync(cancellationToken);
                await LoadHistoryAsync(cancellationToken);
                _ = CompleteDeferredStartupAsync(cancellationToken);
                _ = CompleteDeferredAutomaticUpdateAsync(cancellationToken);
            }

            await CheckForRunningUnoptimizedGameAsync(cancellationToken);
        }

        UpdateSessionControls();
    }

    private async Task CheckForRunningUnoptimizedGameAsync(
        CancellationToken cancellationToken)
    {
        if (_activeSession is not null || _profiles.Count == 0)
        {
            _detectedRunningUnoptimizedGame = null;
            _detectedRunningProcessId = 0;
            if (UnoptimizedGameBanner is not null)
            {
                UnoptimizedGameBanner.Visibility = Visibility.Collapsed;
            }

            return;
        }

        try
        {
            List<ProfileListItem> candidateProfiles = [.. _profiles];
            (ProfileListItem? matched, int pid) = await Task.Run<(ProfileListItem?, int)>(() =>
            {
                Process[] processes = Process.GetProcesses();
                try
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (process.HasExited)
                            {
                                continue;
                            }

                            string? processExecutablePath = null;
                            try
                            {
                                processExecutablePath = process.MainModule?.FileName;
                            }
                            catch
                            {
                            }

                            string processName = process.ProcessName;

                            foreach (ProfileListItem profile in candidateProfiles)
                            {
                                if (!profile.Profile.IsEnabled)
                                {
                                    continue;
                                }

                                string targetExe = profile.ExecutablePath;
                                string targetName = Path.GetFileNameWithoutExtension(targetExe);

                                bool isMatch = false;
                                if (!string.IsNullOrWhiteSpace(processExecutablePath)
                                    && string.Equals(processExecutablePath, targetExe, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }
                                else if (string.Equals(processName, targetName, StringComparison.OrdinalIgnoreCase))
                                {
                                    isMatch = true;
                                }

                                if (isMatch)
                                {
                                    return (profile, process.Id);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    foreach (Process p in processes)
                    {
                        p.Dispose();
                    }
                }

                return (null, 0);
            }, cancellationToken);

            _detectedRunningUnoptimizedGame = matched;
            _detectedRunningProcessId = pid;

            if (UnoptimizedGameBanner is not null)
            {
                if (matched is not null && _activeSession is null)
                {
                    UnoptimizedGameNameText.Text = $"„{matched.DisplayName}” (PID: {pid})";
                    UnoptimizedGameDetailsText.Text =
                        $"Gra {matched.DisplayName} działa w systemie Windows. Możesz włączyć profil optymalizacji w locie bez jej restartowania.";
                    UnoptimizedGameBanner.Visibility = Visibility.Visible;
                }
                else
                {
                    UnoptimizedGameBanner.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async void OnOptimizeRunningGameClicked(
        object sender,
        RoutedEventArgs args)
    {
        ProfileListItem? target = _detectedRunningUnoptimizedGame
            ?? (ProfilesList.SelectedItem as ProfileListItem);
        if (target is null || _isBusy)
        {
            return;
        }

        TacticalAudioService.Instance.PlayClick();
        ShowInfo(
            DashboardInfoBar,
            InfoBarSeverity.Informational,
            "Optymalizacja w locie",
            $"Dołączam do gry „{target.DisplayName}” (PID: {_detectedRunningProcessId}) i aktywuję profil optymalizacji...");

        try
        {
            SetBusy(true);
            SessionPlanClientSnapshot plan = await _sessions.PrepareAsync(
                target.Profile.ProfileId.Value,
                backgroundApplications: [],
                GamePriorityClientMode.Normal,
                _lifetime.Token,
                useSavedBackgroundRules: true);

            _pendingPlan = plan;
            ApplyPreparedPlan(plan);

            SessionStateClientSnapshot active = await _sessions.StartAsync(
                plan.PlanId,
                plan.SessionId,
                _lifetime.Token,
                enableFrameRateTracking: FpsTrackingToggleSwitch.IsOn,
                enableProBalance: ProBalanceToggleSwitch.IsOn);

            _sessionEndpointAvailable = true;
            _pendingPlan = null;
            _planActions.Clear();
            _activeSession = active;
            _detectedRunningUnoptimizedGame = null;
            if (UnoptimizedGameBanner is not null)
            {
                UnoptimizedGameBanner.Visibility = Visibility.Collapsed;
            }

            ApplyActiveSession(active);
            UpdateSessionControls();

            TacticalAudioService.Instance.PlaySessionStart();
            TacticalVfxService.Instance.AnimateQuickFlash(DashboardHeroCard);

            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Success,
                "Optymalizacja aktywna w locie",
                $"Pomyślnie zoptymalizowano działającą grę „{target.DisplayName}”.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            if (IsSessionConnectionFailure(exception))
            {
                _sessionEndpointAvailable = false;
                ApplySessionUnavailable(exception.Message);
            }

            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Error,
                "Błąd optymalizacji w locie",
                exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyPreparedPlan(SessionPlanClientSnapshot plan)
    {
        _planActions.Clear();
        foreach (SessionPlanActionClientSnapshot action in plan.Actions)
        {
            _planActions.Add(new(action));
        }

        PlanStateTitleText.Text = $"Plan gotowy: {plan.GameDisplayName}";
        PlanDetailsText.Text =
            $"Wygasa {plan.ExpiresAtUtc.ToLocalTime():t}. "
            + plan.SafetyMessage;
        PlanActionsList.Visibility = _planActions.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateSessionControls();
    }

    private void ApplyActiveSession(SessionStateClientSnapshot session)
    {
        _ = TryRenewActiveGameLeaseAsync(session, _lifetime.Token);
        if (_dashboardFrameSessionId != session.SessionId)
        {
            _dashboardFrameSessionId = session.SessionId;
            _dashboardFrameTimes.Clear();
            DashboardFrameTimeGraph.Points.Clear();
            TacticalAudioService.Instance.PlaySessionStart();
            TacticalVfxService.Instance.AnimateQuickFlash(DashboardHeroCard);
        }

        string stateLabel = DescribeSessionState(session.State);
        ActiveSessionStateText.Text = stateLabel;
        UpdateDashboardRecoveryState(session);
        ActiveSessionDetailsText.Text =
            $"Od {session.StartedAtUtc.ToLocalTime():g}. "
            + $"Zastosowane akcje: {session.AppliedActionCount}. "
            + session.Message;
        if (FpsTrackingToggleSwitch.IsOn
            && session.FramesPerSecond is double framesPerSecond
            && double.IsFinite(framesPerSecond)
            && framesPerSecond > 0)
        {
            SetDashboardTelemetryAvailability(hasValidSample: true);
            SetDashboardFrameRateStatus(
                "Pomiar aktywny",
                DashboardStatusKind.Ready);
            ActiveFpsText.Text = $"{framesPerSecond:0}";
            ActiveFrameTimeText.Text =
                session.FrameTimeMilliseconds is double frameTime
                    ? $"{frameTime:0.0} ms"
                    : "— ms";
            if (session.FrameTimeMilliseconds is double frameTimeSample)
            {
                AppendDashboardFrameTime(frameTimeSample);
            }
        }
        else
        {
            bool trackingDisabled = !FpsTrackingToggleSwitch.IsOn;
            SetDashboardTelemetryAvailability(hasValidSample: false);
            SetDashboardFrameRateStatus(
                trackingDisabled
                    ? "Wyłączony"
                    : session.State == "Active"
                        ? "Oczekiwanie na klatki"
                        : stateLabel,
                trackingDisabled
                    ? DashboardStatusKind.Pending
                    : session.State == "Active"
                    ? DashboardStatusKind.Warning
                    : DashboardStatusKind.Pending);
            ActiveFpsText.Text = "—";
            ActiveFrameTimeText.Text = "— ms";
        }

        ActiveFrameRateStatusText.Text = !FpsTrackingToggleSwitch.IsOn
            ? "Pomiar FPS jest wyłączony w ustawieniach GameShift."
            : (string.IsNullOrWhiteSpace(session.FrameRateStatus)
                ? "PresentMon nie zwrócił stanu pomiaru."
                : session.FrameRateStatus)
                + (session.FrameRateProcessId is int processId
                    ? $" Proces gry: PID {processId}."
                    : string.Empty);
        UpdatePerformanceOverlay();
        PlanStateTitleText.Text =
            $"Sesja aktywna: {session.GameDisplayName}";
        PlanDetailsText.Text =
            "Gra jest monitorowana lokalnie. Po jej wyjściu GameShift "
            + "automatycznie przywróci zamknięte aplikacje. "
            + DescribeFrameRate(session);
        PlanActionsList.Visibility = Visibility.Collapsed;
        UpdateDashboardHero();
        UpdateSessionControls();
    }

    private async Task TryExportKnownGamesAsync(
        IReadOnlyList<ProfileListItem> profiles,
        CancellationToken cancellationToken)
    {
        try
        {
            await _memoryOptimizerGameStateExporter.ExportKnownGamesAsync(
                profiles.Select(item => new KnownGameExportItem(
                    item.Profile.ProfileId.Value,
                    item.DisplayName,
                    item.ExecutablePath)),
                cancellationToken);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            Debug.WriteLine(
                "Memory Optimizer known-games export failed: " +
                exception.Message);
        }
    }

    private async Task TryRenewActiveGameLeaseAsync(
        SessionStateClientSnapshot session,
        CancellationToken cancellationToken)
    {
        int? processId = session.FrameRateProcessId;
        if (processId is null or <= 0)
        {
            ProfileListItem? profile = _profiles.FirstOrDefault(item =>
                item.Profile.ProfileId.Value == session.ProfileId);
            if (profile is not null)
            {
                string processName = Path.GetFileNameWithoutExtension(
                    profile.ExecutablePath);
                using Process? process = Process.GetProcessesByName(processName)
                    .FirstOrDefault();
                processId = process?.Id;
            }
        }

        if (processId is null or <= 0)
        {
            return;
        }

        try
        {
            await _memoryOptimizerGameStateExporter.RenewActiveGameLeaseAsync(
                session.ProfileId,
                session.GameDisplayName,
                processId.Value,
                cancellationToken);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            Debug.WriteLine(
                "Memory Optimizer active-game export failed: " +
                exception.Message);
        }
    }

    private void OnDashboardFrameTimeCanvasSizeChanged(
        object sender,
        SizeChangedEventArgs args) =>
        RenderDashboardFrameTimeGraph();

    private void AppendDashboardFrameTime(double frameTimeMilliseconds)
    {
        if (!double.IsFinite(frameTimeMilliseconds)
            || frameTimeMilliseconds <= 0)
        {
            return;
        }

        _dashboardFrameTimes.Enqueue(frameTimeMilliseconds);
        while (_dashboardFrameTimes.Count > DashboardFrameHistoryLimit)
        {
            _dashboardFrameTimes.Dequeue();
        }

        RenderDashboardFrameTimeGraph();
    }

    private void RenderDashboardFrameTimeGraph()
    {
        double width = DashboardFrameTimeCanvas.ActualWidth;
        double height = DashboardFrameTimeCanvas.ActualHeight;
        if (width <= 1 || height <= 1 || _dashboardFrameTimes.Count < 2)
        {
            return;
        }

        double[] samples = [.. _dashboardFrameTimes];
        double ceiling = Math.Max(20, samples.Max() * 1.15);
        DashboardFrameTimeGraph.Points.Clear();
        for (int index = 0; index < samples.Length; index++)
        {
            double x = width * index / (samples.Length - 1);
            double normalized = Math.Clamp(samples[index] / ceiling, 0, 1);
            double y = height - (normalized * height);
            DashboardFrameTimeGraph.Points.Add(
                new global::Windows.Foundation.Point(x, y));
        }
    }

    private static string DescribeFrameRate(
        SessionStateClientSnapshot session) =>
        session.FramesPerSecond is double framesPerSecond
            ? $"Pomiar: {framesPerSecond:0.0} FPS"
                + (session.FrameTimeMilliseconds is double frameTime
                    ? $" • {frameTime:0.00} ms."
                    : ".")
            : string.IsNullOrWhiteSpace(session.FrameRateStatus)
                ? "Pomiar: brak potwierdzonych danych."
                : $"Pomiar: {session.FrameRateStatus}";

    private async Task LoadPerformanceOverlayPreferencesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            PerformanceOverlayPreferences preferences =
                await _userDataStore
                    .LoadPerformanceOverlayPreferencesAsync(cancellationToken);
            _isLoadingOverlayPreferences = true;
            SetFpsTrackingToggleValues(preferences.IsFpsTrackingEnabled);
            SetFpsOverlayToggleValues(preferences.IsEnabled);
            OverlayOpacitySlider.Value = preferences.OpacityPercent;
            OverlaySizeSlider.Value = preferences.ScalePercent;
            OverlayCornerSelector.SelectedIndex =
                GetOverlayCornerIndex(preferences.Corner);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Nie wczytano ustawień nakładki",
                "GameShift użyje ustawień domyślnych. "
                + exception.Message);
        }
        finally
        {
            _isLoadingOverlayPreferences = false;
        }

        ApplyOverlaySettingsFromControls(scheduleSave: false);
    }

    private void ApplyOverlaySettingsFromControls(bool scheduleSave)
    {
        if (FpsTrackingToggleSwitch is null
            || FpsOverlayToggleSwitch is null
            || OverlayOpacitySlider is null
            || OverlaySizeSlider is null
            || OverlayCornerSelector is null
            || OverlayOpacityValueText is null
            || OverlaySizeValueText is null)
        {
            return;
        }

        int opacityPercent = GetOverlayOpacityPercent();
        int scalePercent = GetOverlayScalePercent();
        OverlayOpacityValueText.Text = $"{opacityPercent}%";
        OverlaySizeValueText.Text = $"{scalePercent}%";
        if (!FpsTrackingToggleSwitch.IsOn)
        {
            ClearFrameRateTelemetryForDisabledTracking();
        }
        UpdatePerformanceOverlay();

        if (scheduleSave
            && _isLoaded
            && !_isLoadingOverlayPreferences
            && !_lifetime.IsCancellationRequested)
        {
            _overlaySettingsSaveTimer.Stop();
            _overlaySettingsSaveTimer.Start();
        }
    }

    private async void OnOverlaySettingsSaveTick(
        object? sender,
        object args)
    {
        _overlaySettingsSaveTimer.Stop();
        if (_disposed || _lifetime.IsCancellationRequested)
        {
            return;
        }

        if (_isOverlaySettingsSaveRunning)
        {
            _overlaySettingsSaveTimer.Start();
            return;
        }

        _isOverlaySettingsSaveRunning = true;
        try
        {
            await _userDataStore.SavePerformanceOverlayPreferencesAsync(
                CreatePerformanceOverlayPreferencesFromControls(),
                _lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Nie zapisano ustawień nakładki",
                exception.Message);
        }
        finally
        {
            _isOverlaySettingsSaveRunning = false;
        }
    }

    private PerformanceOverlayPreferences
        CreatePerformanceOverlayPreferencesFromControls() =>
        new(
            FpsOverlayToggleSwitch.IsOn,
            FpsTrackingToggleSwitch.IsOn,
            GetOverlayOpacityPercent(),
            GetOverlayScalePercent(),
            GetSelectedOverlayCorner(),
            PerformanceOverlayStyle.FullDeck,
            PerformanceOverlayTheme.CyberNeon,
            DateTimeOffset.UtcNow);

    private int GetOverlayOpacityPercent() =>
        Math.Clamp(
            (int)Math.Round(
                OverlayOpacitySlider.Value,
                MidpointRounding.AwayFromZero),
            PerformanceOverlayPreferences.MinimumOpacityPercent,
            PerformanceOverlayPreferences.MaximumOpacityPercent);

    private int GetOverlayScalePercent() =>
        Math.Clamp(
            (int)Math.Round(
                OverlaySizeSlider.Value,
                MidpointRounding.AwayFromZero),
            PerformanceOverlayPreferences.MinimumScalePercent,
            PerformanceOverlayPreferences.MaximumScalePercent);

    private PerformanceOverlayCorner GetSelectedOverlayCorner() =>
        (OverlayCornerSelector.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() switch
        {
            nameof(PerformanceOverlayCorner.TopLeft) =>
                PerformanceOverlayCorner.TopLeft,
            nameof(PerformanceOverlayCorner.BottomLeft) =>
                PerformanceOverlayCorner.BottomLeft,
            nameof(PerformanceOverlayCorner.BottomRight) =>
                PerformanceOverlayCorner.BottomRight,
            _ => PerformanceOverlayCorner.TopRight,
        };

    private static int GetOverlayCornerIndex(
        PerformanceOverlayCorner corner) =>
        corner switch
        {
            PerformanceOverlayCorner.TopLeft => 0,
            PerformanceOverlayCorner.BottomLeft => 2,
            PerformanceOverlayCorner.BottomRight => 3,
            _ => 1,
        };

    private void UpdatePerformanceOverlay()
    {
        if (FpsTrackingToggleSwitch.IsOn is false
            || FpsOverlayToggleSwitch.IsOn is false)
        {
            _overlayPollTimer.Stop();
            HidePerformanceOverlay();
            return;
        }

        // Sesja, gdy jest — ale nakladka nie moze od niej zalezec. Gra
        // uruchomiona poza GameShiftem to normalny przypadek, a pokazywanie
        // wtedy myslnikow czyta sie jako zepsute i bylo zepsute.
        if (_activeSession is null)
        {
            _overlayPollTimer.Start();
            return;
        }

        _overlayPollTimer.Stop();
        try
        {
            _performanceOverlay ??= new();
            _performanceOverlay.Update(_activeSession);
            _performanceOverlay.ApplyPreferences(
                CreatePerformanceOverlayPreferencesFromControls());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
            _performanceOverlay?.Dispose();
            _performanceOverlay = null;
            ActiveFrameRateStatusText.Text +=
                $" Nakładka jest niedostępna: {exception.Message}";
        }
    }

    /// <summary>
    /// Feeds the overlay from whatever is in the foreground when no session is
    /// running. One sample at a time: PresentMon takes a moment to answer, and
    /// stacking requests would leave the overlay drawing stale numbers while
    /// queued work piled up behind it.
    /// </summary>
    private async void OnOverlayPollTick(object? sender, object args)
    {
        if (_overlayPollInFlight || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _overlayPollInFlight = true;
        try
        {
            _foregroundGameWatcher ??= new();
            ForegroundGameSample sample = await _foregroundGameWatcher
                .SampleAsync(_lifetime.Token);

            _performanceOverlay ??= new();
            _performanceOverlay.Update(
                sample.ProcessName,
                sample.ProcessId,
                sample.FramesPerSecond,
                sample.FrameTimeMilliseconds);
            _performanceOverlay.ApplyPreferences(
                CreatePerformanceOverlayPreferencesFromControls());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException)
        {
            _overlayPollTimer.Stop();
            _performanceOverlay?.Dispose();
            _performanceOverlay = null;
        }
        finally
        {
            _overlayPollInFlight = false;
        }
    }

    private void HidePerformanceOverlay() =>
        _performanceOverlay?.SetRequestedVisibility(false);

    private void ClearFrameRateTelemetryForDisabledTracking()
    {
        _dashboardFrameTimes.Clear();
        DashboardFrameTimeGraph.Points.Clear();
        ActiveFpsText.Text = "—";
        ActiveFrameTimeText.Text = "— ms";
        ActiveFrameRateStatusText.Text =
            "Pomiar FPS jest wyłączony w ustawieniach GameShift.";
        SetDashboardTelemetryAvailability(hasValidSample: false);
        SetDashboardFrameRateStatus(
            "Wyłączony",
            DashboardStatusKind.Pending);
    }

    private void ApplyCompletedSession(SessionStateClientSnapshot session)
    {
        TacticalAudioService.Instance.PlaySessionStop();
        ActiveSessionStateText.Text = "Gotowość";
        ActiveSessionDetailsText.Text = session.Message;
        HidePerformanceOverlay();
        ResetFrameRateDisplay();
        UpdateDashboardRecoveryState(session);
        PlanStateTitleText.Text =
            $"Sesja zakończona: {session.GameDisplayName}";
        PlanDetailsText.Text =
            "Trwały journal został uzgodniony, a podsumowanie zapisane "
            + $"w lokalnej historii. Przywrócone aplikacje: "
            + $"{session.RestoredActionCount}.";
        PlanActionsList.Visibility = Visibility.Collapsed;
        UpdateDashboardHero();
        UpdateSessionControls();
    }

    private void ApplyNoActiveSession()
    {
        ActiveSessionStateText.Text = "Gotowość";
        ActiveSessionDetailsText.Text =
            "SessionHost nie monitoruje obecnie żadnej gry.";
        HidePerformanceOverlay();
        ResetFrameRateDisplay();
        UpdateDashboardRecoveryState(session: null);

        if (_pendingPlan is not null)
        {
            ApplyPreparedPlan(_pendingPlan);
            return;
        }

        PlanStateTitleText.Text = "Brak aktywnego planu";
        PlanDetailsText.Text =
            "Wybierz grę, przeanalizuj aplikacje w tle i zaznacz te, "
            + "których nie potrzebujesz podczas grania.";
        PlanActionsList.Visibility = Visibility.Collapsed;
        UpdateDashboardHero();
    }

    private void SetDashboardTelemetryAvailability(bool hasValidSample)
    {
        DashboardFpsNoDataState.Visibility = hasValidSample
            ? Visibility.Collapsed
            : Visibility.Visible;
        DashboardFpsTelemetryContent.Visibility = hasValidSample
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ResetFrameRateDisplay()
    {
        _dashboardFrameSessionId = null;
        _dashboardFrameTimes.Clear();
        DashboardFrameTimeGraph.Points.Clear();
        ActiveFpsText.Text = "—";
        ActiveFrameTimeText.Text = "— ms";
        ActiveFrameRateStatusText.Text = FpsTrackingToggleSwitch.IsOn
            ? "PresentMon uruchomi się automatycznie razem z grą."
            : "Pomiar FPS jest wyłączony w ustawieniach GameShift.";
        SetDashboardTelemetryAvailability(hasValidSample: false);
        SetDashboardFrameRateStatus(
            FpsTrackingToggleSwitch.IsOn ? "Oczekuje" : "Wyłączony",
            DashboardStatusKind.Pending);
    }

    private void ApplySessionUnavailable(string message)
    {
        bool invalidatedPreparedPlan = _pendingPlan is not null;
        if (invalidatedPreparedPlan)
        {
            _pendingPlan = null;
            _planActions.Clear();
            PlanActionsList.Visibility = Visibility.Collapsed;
        }

        ActiveSessionStateText.Text = "Niedostępna";
        ActiveSessionDetailsText.Text =
            $"Nie można potwierdzić stanu SessionHost. {message}";
        ActiveFpsText.Text = "—";
        ActiveFrameTimeText.Text = "— ms";
        ActiveFrameRateStatusText.Text =
            "Brak połączenia z SessionHost; GameShift nie pokazuje "
            + "niepotwierdzonego FPS.";
        SetDashboardTelemetryAvailability(hasValidSample: false);
        SetDashboardFrameRateStatus(
            "Niedostępny",
            DashboardStatusKind.Error);
        UpdateDashboardRecoveryState(_activeSession);
        HidePerformanceOverlay();
        if (_activeSession is null)
        {
            PlanStateTitleText.Text = invalidatedPreparedPlan
                ? "Plan unieważniony po utracie połączenia"
                : "SessionHost jest niedostępny";
            PlanDetailsText.Text = invalidatedPreparedPlan
                ? "Przygotuj nowy plan po odzyskaniu połączenia. "
                    + "GameShift nie uruchomi planu, którego host nie może "
                    + "już potwierdzić."
                : "Uruchom SessionHost, aby przygotować bezpieczny plan "
                    + "i sprawdzić aktywną sesję.";
        }
    }

    private void ClearPreparedPlan()
    {
        _pendingPlan = null;
        _planActions.Clear();
        ApplyNoActiveSession();
    }

    private static string DescribeSessionState(string state) =>
        state switch
        {
            "Active" => "aktywna",
            "Completed" => "zakończona",
            "Restoring" => "przywracanie",
            "Reconciling" => "uzgadnianie",
            "GameExited" => "gra zakończona",
            _ => state,
        };

    private async Task LoadProfilesAsync(
        CancellationToken cancellationToken)
    {
        Guid? selectedProfileId =
            (ProfilesList.SelectedItem as ProfileListItem)?
                .Profile.ProfileId.Value;
        IReadOnlyList<ManualGameProfile> profiles;
        IReadOnlyList<GameMetadata> metadata;
        try
        {
            profiles = await _userDataStore.ListAsync(cancellationToken);
            metadata =
                await _userDataStore.ListMetadataAsync(cancellationToken);
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            // Baza profili moze byc zajeta albo uszkodzona. Wolimy okno
            // z pusta biblioteka i wyjasnieniem niz proces, ktory znika przed
            // pokazaniem czegokolwiek — ta metoda biegnie na sciezce startowej
            // wewnatrz async void.
            ShowInfo(
                DashboardInfoBar,
                InfoBarSeverity.Error,
                "Nie udało się wczytać biblioteki gier",
                exception.Message);
            return;
        }
        // Nie ToDictionary: dwa wpisy metadanych dla jednego profilu rzucaja
        // ArgumentException, a stad jest tylko do async void na sciezce
        // startowej. Uszkodzony wiersz w bazie zamykalby wtedy droge do
        // uruchomienia programu na stale, bez zadnego komunikatu. Wygrywa
        // ostatni wpis; gorsze niz wybor jest brak okna.
        Dictionary<Core.Domain.Identifiers.GameProfileId, GameMetadata>
            metadataByProfile = [];
        foreach (GameMetadata entry in metadata)
        {
            metadataByProfile[entry.ProfileId] = entry;
        }
        _profiles.Clear();
        List<ProfileListItem> profileItems = new(profiles.Count);
        foreach (ManualGameProfile profile in profiles)
        {
            metadataByProfile.TryGetValue(
                profile.ProfileId,
                out GameMetadata? profileMetadata);
            ProfileListItem item = new(profile, profileMetadata);
            profileItems.Add(item);
            _profiles.Add(item);
        }

        if (selectedProfileId is not null)
        {
            ProfilesList.SelectedItem = _profiles.FirstOrDefault(item =>
                item.Profile.ProfileId.Value == selectedProfileId.Value);
        }
        else if (_profiles.Count > 0)
        {
            ProfilesList.SelectedItem = _profiles[0];
        }

        _isSynchronizingProfileSelection = true;
        GameProfileSelector.SelectedItem = ProfilesList.SelectedItem;
        DashboardGameRail.SelectedItem = ProfilesList.SelectedItem;
        _isSynchronizingProfileSelection = false;
        UpdateDashboardRailSelection();

        ProfilesEmptyText.Visibility = _profiles.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfileCountText.Text = _profiles.Count.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        UpdateDashboardHero();
        UpdateSessionControls();

        await TryExportKnownGamesAsync(
            profileItems,
            cancellationToken);

        await Task.WhenAll(profileItems.Select(async item =>
        {
            item.PosterArtworkSource = await LocalArtworkImageLoader.LoadAsync(
                item.PosterArtworkPath,
                cancellationToken);
            item.HeroArtworkSource = await LocalArtworkImageLoader.LoadAsync(
                item.HeroArtworkPath,
                cancellationToken);
        }));
        UpdateDashboardHero();
        await CheckForRunningUnoptimizedGameAsync(cancellationToken);
    }

    private void UpdateDashboardRailSelection()
    {
        ProfileListItem? selected =
            DashboardGameRail.SelectedItem as ProfileListItem;
        foreach (ProfileListItem item in _profiles)
        {
            item.DashboardSelectionOpacity = ReferenceEquals(item, selected)
                ? 1
                : 0;
        }
    }

    private async Task LoadHistoryAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Core.History.SessionSummary> history =
            await _userDataStore.ListRecentAsync(
                MaximumRecentHistoryItems,
                cancellationToken);
        _history.Clear();
        foreach (Core.History.SessionSummary summary in history)
        {
            _history.Add(new(summary));
        }

        HistoryEmptyText.Visibility = _history.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryCountText.Text = _history.Count.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        UpdateHistorySummary();
    }

    /// <summary>
    /// Summarises the sessions above the log. On its own the list answers
    /// "what happened last time"; the strip answers "what has GameShift
    /// actually done for me", which the page could not show before.
    /// </summary>
    private void UpdateHistorySummary()
    {
        if (HistorySummaryPanel is null)
        {
            return;
        }

        if (_history.Count == 0)
        {
            HistorySummaryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        HistorySummaryPanel.Visibility = Visibility.Visible;

        TimeSpan played = TimeSpan.Zero;
        int appliedActions = 0;
        int troubled = 0;
        foreach (HistoryListItem item in _history)
        {
            Core.History.SessionSummary summary = item.Summary;
            played += summary.EndedAtUtc - summary.StartedAtUtc;
            appliedActions += summary.AppliedActionCount;
            if (summary.ErrorCount > 0 || summary.ConflictCount > 0)
            {
                troubled++;
            }
        }

        HistorySessionsValueText.Text = _history.Count.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        HistoryPlaytimeValueText.Text = played.TotalHours >= 1
            ? $"{played.TotalHours:0.#} godz."
            : $"{played.TotalMinutes:0} min";
        HistoryActionsValueText.Text = appliedActions.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        HistoryTroubledValueText.Text = troubled.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        HistoryTroubledValueText.Foreground = troubled > 0
            ? (Brush)Application.Current.Resources["GameShiftAmberBrush"]
            : (Brush)Application.Current.Resources["GameShiftSuccessBrush"];
    }

    private void UpdateDashboardHero()
    {
        if (_activeSession is not null)
        {
            ProfileListItem? activeProfile = _profiles.FirstOrDefault(item =>
                item.Profile.ProfileId.Value == _activeSession.ProfileId);
            DashboardHeroStatusText.Text = "Sesja aktywna";
            DashboardHeroGameText.Text = _activeSession.GameDisplayName;
            DashboardHeroPathText.Text =
                "Gra jest monitorowana. Po ręcznym wyjściu GameShift "
                + "automatycznie zatrzyma pomiar i przywróci wszystkie "
                + "journalowane działania.";
            DashboardHeroSourceText.Text = activeProfile?.SourceLabel
                ?? "Gra lokalna";
            DashboardHeroPresetText.Text = activeProfile?.PresetLabel
                ?? "Aktywna sesja";
            DashboardHeroVerifiedText.Text = "Tożsamość potwierdzona";
            DashboardHeroLastPlayedText.Text = "Sesja uruchomiona teraz";
            DashboardHeroPlaytimeText.Text = activeProfile?.PlaytimeLabel
                ?? "Czas bieżącej sesji jest mierzony";
            SetDashboardArtwork(activeProfile);
            return;
        }

        if (ProfilesList.SelectedItem is ProfileListItem selected)
        {
            DashboardHeroStatusText.Text = selected.IsLaunchEnabled
                ? "Wybrana gra"
                : "Profil wyłączony";
            DashboardHeroGameText.Text = selected.DisplayName;
            DashboardHeroPathText.Text =
                "Zoptymalizowany start lokalny. Zatwierdzone działania "
                + "są wykonywane sekwencyjnie w tle po uruchomieniu gry.";
            DashboardHeroSourceText.Text = selected.Metadata is null
                ? DescribeProfileSource(selected.ExecutablePath)
                : selected.SourceLabel;
            DashboardHeroPresetText.Text = selected.PresetLabel;
            DashboardHeroVerifiedText.Text = selected.IsLaunchEnabled
                ? "Zweryfikowany EXE"
                : "Profil wyłączony";
            DashboardHeroLastPlayedText.Text = selected.LastPlayedLabel;
            DashboardHeroPlaytimeText.Text = selected.PlaytimeLabel;
            ToolTipService.SetToolTip(
                DashboardHeroGameText,
                selected.ExecutablePath);
            SetDashboardArtwork(selected);
            return;
        }

        DashboardHeroStatusText.Text = "Biblioteka pusta";
        DashboardHeroGameText.Text = "Dodaj lub wykryj grę";
        DashboardHeroPathText.Text =
            "GameShift obsługuje biblioteki Steam, Epic, GOG, Xbox "
            + "i ręcznie wskazane pliki EXE.";
        DashboardHeroSourceText.Text = "Biblioteka lokalna";
        DashboardHeroPresetText.Text = "Brak profilu";
        DashboardHeroVerifiedText.Text = "Oczekuje na grę";
        DashboardHeroLastPlayedText.Text = "Ostatnio grano: —";
        DashboardHeroPlaytimeText.Text = "Czas gry: —";
        ToolTipService.SetToolTip(DashboardHeroGameText, null);
        SetDashboardArtwork(null);
    }

    private void SetDashboardArtwork(ProfileListItem? profile)
    {
        DashboardHeroImage.Source = profile?.HeroArtworkSource
            ?? profile?.PosterArtworkSource
            ?? _dashboardFallbackArtwork;
    }

    private static string DescribeProfileSource(string executablePath)
    {
        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}steamapps"
                + $"{Path.DirectorySeparatorChar}common"
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Steam";
        }

        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}Epic Games"
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Epic Games";
        }

        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}GOG Galaxy"
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return "GOG";
        }

        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}Roblox"
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Roblox";
        }

        if (executablePath.Contains(
                $"{Path.DirectorySeparatorChar}XboxGames"
                + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return "Xbox";
        }

        return "Profil EXE";
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        GlobalProgress.IsActive = isBusy;
        GlobalProgress.Opacity = isBusy ? 1 : 0;
        AddProfileButton.IsEnabled = !isBusy;
        DiscoverGamesButton.IsEnabled = !isBusy;
        ProfilesList.IsEnabled = !isBusy;
        UpdateSessionControls();
    }

    private void UpdateSessionControls()
    {
        bool hasSelectedProfile =
            ProfilesList.SelectedItem is ProfileListItem;
        bool canPrepare =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession is null
            && hasSelectedProfile;
        bool hasUsablePlan =
            _pendingPlan is not null
            && _pendingPlan.ExpiresAtUtc >= DateTimeOffset.UtcNow;

        DeleteProfileButton.IsEnabled =
            !_isBusy && hasSelectedProfile;
        AnalyzeBackgroundApplicationsButton.IsEnabled =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession is null
            && hasSelectedProfile;
        BackgroundApplicationsList.IsEnabled =
            !_isBusy && _activeSession is null;
        OptimizationIntensitySelector.IsEnabled =
            !_isBusy && _activeSession is null;
        PrepareSelectedProfileButton.IsEnabled = canPrepare;
        DashboardHeroPlayButton.IsEnabled = canPrepare;
        PreparePlanButton.IsEnabled = canPrepare;
        StartSessionButton.Visibility = _pendingPlan is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        StartSessionButton.IsEnabled =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession is null
            && hasUsablePlan;
        RestoreSessionButton.Visibility = _activeSession is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RestoreSessionButton.IsEnabled =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession is not null;
        DashboardRestoreSessionButton.IsEnabled =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession is not null;
        CloseGameButton.Visibility = _activeSession is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        CloseGameButton.IsEnabled =
            !_isBusy
            && _sessionEndpointAvailable
            && _activeSession?.State == "Active";
    }

    private void UpdateBackgroundApplicationSummary()
    {
        BackgroundApplicationListItem[] selected =
            _backgroundApplications
                .Where(application => application.IsSelected)
                .ToArray();
        long estimatedSavingsBytes = selected.Sum(application =>
        {
            BackgroundProcessActionMode mode = application.SelectedAction.Mode switch
            {
                BackgroundProcessClientActionMode.CloseAndRestore =>
                    BackgroundProcessActionMode.CloseAndRestore,
                BackgroundProcessClientActionMode.LowerPriorityAndEcoQos =>
                    BackgroundProcessActionMode.LowerPriorityAndEcoQos,
                _ => BackgroundProcessActionMode.LowerPriority,
            };
            return BackgroundOptimizationAdvisor.EstimatePotentialMemorySavings(
                application.Process.WorkingSetBytes,
                mode);
        });

        SelectedAppsCountText.Text =
            selected.Length.ToString(
                System.Globalization.CultureInfo.CurrentCulture);
        SelectedAppsMemoryText.Text =
            FormatMemory(estimatedSavingsBytes);
        BackgroundApplicationsEmptyPanel.Visibility =
            _backgroundApplications.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        BackgroundApplicationsEmptyText.Visibility =
            _backgroundApplications.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        BackgroundApplicationsList.Visibility =
            _backgroundApplications.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void OnApplySmartRecommendationsClicked(object sender, RoutedEventArgs args)
    {
        bool isAggressive = IsAggressiveOptimizationEnabled();
        foreach (BackgroundApplicationListItem item in _backgroundApplications)
        {
            if (BackgroundOptimizationAdvisor.ShouldRecommendOptimization(
                    item.Process.Name,
                    item.Process.WorkingSetBytes,
                    item.Process.HasMainWindow,
                    isAggressive))
            {
                BackgroundProcessActionMode recommended =
                    BackgroundOptimizationAdvisor.RecommendAction(
                        item.Process.Name,
                        item.Process.HasMainWindow,
                        canClose: item.AvailableActions.Any(a => a.Mode == BackgroundProcessClientActionMode.CloseAndRestore),
                        isAggressive: isAggressive);

                BackgroundProcessClientActionMode clientMode = recommended switch
                {
                    BackgroundProcessActionMode.CloseAndRestore => BackgroundProcessClientActionMode.CloseAndRestore,
                    _ => BackgroundProcessClientActionMode.LowerPriorityAndEcoQos,
                };

                item.ApplySavedAction(clientMode);
            }
        }

        UpdateBackgroundApplicationSummary();
    }

    private void UpdateDashboardSystemMetrics()
    {
        LocalSystemMetricsSnapshot snapshot = _systemMetrics.Sample();
        if (snapshot.CpuPercent is double cpuPercent)
        {
            DashboardCpuText.Text = $"{cpuPercent:0}%";
            DashboardCpuBar.Value = cpuPercent;
        }

        if (snapshot.TotalPhysicalMemoryBytes is ulong totalMemory
            && snapshot.UsedPhysicalMemoryBytes is ulong usedMemory
            && totalMemory > 0)
        {
            double usedGigabytes = usedMemory / 1_073_741_824d;
            double totalGigabytes = totalMemory / 1_073_741_824d;
            DashboardMemoryText.Text =
                $"{usedGigabytes:0.#} / {totalGigabytes:0.#} GB";
            DashboardMemoryBar.Value =
                Math.Clamp(usedMemory * 100d / totalMemory, 0, 100);
        }

        if (snapshot.SystemDriveUsedPercent is double driveUsedPercent)
        {
            DashboardDiskText.Text = $"{driveUsedPercent:0}%";
            DashboardDiskBar.Value = driveUsedPercent;
        }
    }

    private async void LoadSelectedOptimizationPreferences()
    {
        if (ProfilesList.SelectedItem is not ProfileListItem selected)
        {
            _loadedPreferences = null;
            return;
        }

        Guid expectedProfileId =
            selected.Profile.ProfileId.Value;
        try
        {
            GameOptimizationPreferences preferences =
                await _userDataStore.LoadOptimizationPreferencesAsync(
                    selected.Profile.ProfileId,
                    _lifetime.Token);
            if (_lifetime.IsCancellationRequested
                || ProfilesList.SelectedItem
                    is not ProfileListItem current
                || current.Profile.ProfileId.Value
                    != expectedProfileId)
            {
                return;
            }

            _loadedPreferences = preferences;
            _isLoadingOptimizationPreferences = true;
            // Saved priority values from earlier releases remain visible in
            // storage for compatibility, but do not become an implicit choice
            // in a new session. The user must choose an experimental priority
            // again for each manual plan.
            GamePrioritySelector.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedUiFailure(exception))
        {
            _loadedPreferences = null;
            ShowInfo(
                PlanInfoBar,
                InfoBarSeverity.Warning,
                "Nie wczytano reguł gry",
                exception.Message);
        }
        finally
        {
            _isLoadingOptimizationPreferences = false;
        }
    }

    private int ApplyBackgroundRules()
    {
        if (RememberRulesCheckBox.IsChecked != true
            || _loadedPreferences is null
            || ProfilesList.SelectedItem is not ProfileListItem selected
            || _loadedPreferences.ProfileId
                != selected.Profile.ProfileId)
        {
            return 0;
        }

        Dictionary<string, SavedBackgroundActionMode> rules =
            _loadedPreferences.BackgroundRules.ToDictionary(
                rule => rule.ExecutablePath,
                rule => rule.ActionMode,
                StringComparer.OrdinalIgnoreCase);
        int applied = 0;
        foreach (BackgroundApplicationListItem application
                     in _backgroundApplications)
        {
            if (!rules.TryGetValue(
                    application.Process.ExecutablePath,
                    out SavedBackgroundActionMode savedMode))
            {
                continue;
            }

            if (savedMode == SavedBackgroundActionMode.Ignore)
            {
                application.IsSelected = false;
                applied++;
                continue;
            }

            BackgroundProcessClientActionMode mode = savedMode switch
            {
                SavedBackgroundActionMode.CloseAndRestore =>
                    BackgroundProcessClientActionMode.CloseAndRestore,
                SavedBackgroundActionMode.LowerPriorityAndEcoQos =>
                    BackgroundProcessClientActionMode
                        .LowerPriorityAndEcoQos,
                _ => BackgroundProcessClientActionMode.LowerPriority,
            };
            if (application.ApplySavedAction(mode))
            {
                applied++;
            }
        }

        return applied;
    }

    private bool IsAggressiveOptimizationEnabled() =>
        (OptimizationIntensitySelector.SelectedItem as ComboBoxItem)?
            .Tag?.ToString()
        == "Aggressive";

    private int GetMaximumBackgroundProcessCount() =>
        IsAggressiveOptimizationEnabled()
            ? AggressiveMaximumBackgroundProcesses
            : StandardMaximumBackgroundProcesses;

    private async ValueTask<GameOptimizationPreferences>
        SaveSelectedOptimizationPreferencesAsync(
            Core.Domain.Identifiers.GameProfileId profileId,
            CancellationToken cancellationToken)
    {
        SavedBackgroundProcessRule[] rules =
            _backgroundApplications
                .GroupBy(
                    application => application.Process.ExecutablePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    BackgroundApplicationListItem[] selected =
                        group
                            .Where(application =>
                                application.IsSelected)
                            .ToArray();
                    SavedBackgroundActionMode mode =
                        selected.Length == 0
                            ? SavedBackgroundActionMode.Ignore
                            : selected.Any(application =>
                                application.SelectedAction.Mode
                                    == BackgroundProcessClientActionMode
                                        .CloseAndRestore)
                                ? SavedBackgroundActionMode.CloseAndRestore
                                : selected.Any(application =>
                                    application.SelectedAction.Mode
                                        == BackgroundProcessClientActionMode
                                            .LowerPriorityAndEcoQos)
                                    ? SavedBackgroundActionMode
                                        .LowerPriorityAndEcoQos
                                    : SavedBackgroundActionMode
                                        .LowerPriority;
                    return new SavedBackgroundProcessRule(
                        group.Key,
                        mode);
                })
                .ToArray();
        GameOptimizationPreferences preferences = new(
            profileId,
            GetSelectedGamePriority() switch
            {
                GamePriorityClientMode.Normal =>
                    SavedGamePriorityMode.Normal,
                GamePriorityClientMode.High =>
                    SavedGamePriorityMode.High,
                _ => SavedGamePriorityMode.AboveNormal,
            },
            rules,
            DateTimeOffset.UtcNow);
        await _userDataStore.SaveOptimizationPreferencesAsync(
            preferences,
            cancellationToken);
        return preferences;
    }

    private void ApplyServiceClassifications(
        IReadOnlyList<DiscoveredServiceClientSnapshot> services)
    {
        DiscoveredServiceClientSnapshot[] running = services
            .Where(service =>
                string.Equals(
                    service.Status,
                    "Running",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        int requiredCount = running.Count(service =>
            service.SafetyClassification
                is "RequiredSystem" or "Unsupported");
        int relatedCount = running.Count(service =>
            service.SafetyClassification == "GameInfrastructure");
        int optionalCount = running.Count(service =>
            service.SafetyClassification == "OptionalThirdParty");
        int stoppedCount = services.Count - running.Length;

        ServiceClassificationSummaryText.Text =
            $"Wykryto: {services.Count}  •  uruchomione: {running.Length}  •  "
            + $"zatrzymane: {stoppedCount}\n"
            + $"Wśród uruchomionych — chronione: {requiredCount}  •  "
            + $"powiązane z grami: {relatedCount}  •  "
            + $"opcjonalne firm trzecich: {optionalCount}";

        _serviceClassifications.Clear();
        IEnumerable<DiscoveredServiceClientSnapshot> visibleServices =
            services
                .OrderBy(service =>
                    string.Equals(
                        service.Status,
                        "Running",
                        StringComparison.OrdinalIgnoreCase)
                            ? 0
                            : 1)
                .ThenBy(service =>
                    service.SafetyClassification switch
                    {
                        "OptionalThirdParty" => 0,
                        "GameInfrastructure" => 1,
                        "Unsupported" => 2,
                        _ => 3,
                    })
                .ThenBy(
                    service => service.DisplayName,
                    StringComparer.CurrentCultureIgnoreCase);
        foreach (DiscoveredServiceClientSnapshot service in visibleServices)
        {
            _serviceClassifications.Add(new(service));
        }

        ServiceClassificationsList.Visibility =
            _serviceClassifications.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private static bool IsBackgroundApplicationCandidate(
        DiscoveredProcessClientSnapshot process,
        int currentSessionId,
        string gameExecutablePath)
    {
        ProcessClassification selectedGameClassification =
            ProcessClassificationService.Classify(
                process.Name,
                process.ExecutablePath,
                process.SessionId,
                currentSessionId,
                gameExecutablePath);
        if (process.StartedAtUtc is null
            || selectedGameClassification.Kind
                != ProcessSafetyClassification.OptionalUser
            || process.ProcessId == Environment.ProcessId
            || process.SessionId != currentSessionId
            || process.WorkingSetBytes < 8L * 1024 * 1024
            || string.IsNullOrWhiteSpace(process.ExecutablePath)
            || !Path.IsPathFullyQualified(process.ExecutablePath)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetExtension(process.ExecutablePath),
                ".exe")
            || StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(process.ExecutablePath),
                Path.GetFullPath(gameExecutablePath))
            || process.Name.StartsWith(
                "GameShift",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string windowsDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string relative = Path.GetRelativePath(
            windowsDirectory,
            process.ExecutablePath);
        if ((!relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !Path.IsPathFullyQualified(relative))
            || process.ExecutablePath.Contains(
                $"{Path.DirectorySeparatorChar}WindowsApps"
                    + $"{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return process.Name is not (
            "Agent"
            or "Battle.net"
            or "EABackgroundService"
            or "EADesktop"
            or "EpicGamesLauncher"
            or "GalaxyClient"
            or "RiotClientServices"
            or "RiotClientUx"
            or "steam"
            or "steamwebhelper"
            or "UbisoftConnect"
            or "upc");
    }

    private string BuildStartConfirmation(
        SessionPlanClientSnapshot plan)
    {
        BackgroundApplicationListItem[] selected =
            _backgroundApplications
                .Where(application => application.IsSelected)
                .ToArray();
        if (selected.Length == 0)
        {
            return "GameShift ponownie sprawdzi zatwierdzony plik EXE, "
                + "zapisze trwały punkt recovery i uruchomi grę albo dołączy "
                + "do jednego zgodnego, działającego procesu. "
                + "Nie wybrano aplikacji w tle do zamknięcia ani "
                + "ograniczenia.";
        }

        int closeCount = selected.Count(application =>
            application.SelectedAction.Mode
                == BackgroundProcessClientActionMode.CloseAndRestore);
        int lowerCount = selected.Length - closeCount;
        int ecoQosCount = selected.Count(application =>
            application.SelectedAction.Mode
                == BackgroundProcessClientActionMode
                    .LowerPriorityAndEcoQos);
        long selectedBytes = selected.Sum(application =>
            application.SelectedAction.Mode
                    == BackgroundProcessClientActionMode.CloseAndRestore
                ? application.Process.WorkingSetBytes
                : 0);
        return "GameShift najpierw uruchomi zweryfikowaną grę albo dołączy "
            + "do jednego zgodnego procesu. Następnie sekwencyjnie zamknie "
            + $"aplikacji: {closeCount} "
            + $"(working set około {FormatMemory(selectedBytes)}; nie jest "
            + "to miara FPS), obniży "
            + $"priorytet procesów: {lowerCount}, włączy EcoQoS dla: "
            + $"{ecoQosCount}. "
            + "Po zakończeniu sesji odtworzy poprzedni stan. Niezapisane "
            + "dane mogą wywołać własne pytanie aplikacji o zapis.";
    }

    private GamePriorityClientMode GetSelectedGamePriority() =>
        (GamePrioritySelector.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() switch
        {
            "Normal" => GamePriorityClientMode.Normal,
            "High" => GamePriorityClientMode.High,
            _ => GamePriorityClientMode.AboveNormal,
        };

    private async Task<bool> ConfirmExperimentalGamePriorityAsync(
        GamePriorityClientMode priority)
    {
        string label = priority == GamePriorityClientMode.High
            ? "Wysoki"
            : "Powyżej normalnego";
        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = $"Użyć priorytetu „{label}”?",
            Content = "To ręczny tryb eksperymentalny. GameShift zapisze "
                + "snapshot, zweryfikuje rzeczywistą zmianę w Windows i "
                + "przywróci stan po sesji, ale nie obiecuje wzrostu FPS. "
                + "Szybki Play nie użyje tego ustawienia automatycznie.",
            PrimaryButtonText = "Przygotuj eksperymentalny plan",
            CloseButtonText = "Pozostaw normalny",
            DefaultButton = ContentDialogButton.Close,
        };
        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string FormatMemory(long bytes)
    {
        double mebibytes = Math.Max(0, bytes) / 1024d / 1024d;
        return mebibytes >= 1024
            ? $"{mebibytes / 1024:0.0} GB"
            : $"{mebibytes:0} MB";
    }

    private static void ApplyEndpointSnapshot(
        DiagnosticsEndpointSnapshot endpoint,
        TextBlock stateText,
        TextBlock detailsText,
        TextBlock diagnosticsText)
    {
        if (!endpoint.IsAvailable)
        {
            stateText.Text = "Niedostępny";
            detailsText.Text = endpoint.Message;
            diagnosticsText.Text = "Niedostępny (Offline)";
            return;
        }

        stateText.Text = endpoint.IsCompatible
            ? "Gotowy"
            : "Niezgodna wersja";
        detailsText.Text = endpoint.ProcessCount > 0
            ? $"Monitorowane procesy: {endpoint.ProcessCount}"
            : $"Monitorowane usługi: {endpoint.ServiceCount}";
        diagnosticsText.Text = endpoint.IsCompatible
            ? $"Aktywny i gotowy ({endpoint.LatencyMilliseconds} ms)"
            : $"Wymaga aktualizacji ({endpoint.LatencyMilliseconds} ms)";
    }

    private static void ApplyDashboardEndpointIndicator(
        DiagnosticsEndpointSnapshot endpoint,
        Border indicator,
        FontIcon glyph)
    {
        DashboardStatusKind status = !endpoint.IsAvailable
            ? DashboardStatusKind.Error
            : endpoint.IsCompatible
                ? DashboardStatusKind.Ready
                : DashboardStatusKind.Warning;
        ApplyDashboardStatusIndicator(indicator, glyph, status);
    }

    private void UpdateDashboardRecoveryState(
        SessionStateClientSnapshot? session)
    {
        string label;
        DashboardStatusKind status;
        if (session is not null)
        {
            (label, status) = session.State switch
            {
                "RecoveryRequired" => (
                    "Wymaga działania",
                    DashboardStatusKind.Error),
                "Restoring" or "Reconciling" or "GameExited" => (
                    "Przywracanie",
                    DashboardStatusKind.Warning),
                "Completed" => (
                    "Przywrócone",
                    DashboardStatusKind.Ready),
                "Active" => (
                    "Monitorowane",
                    DashboardStatusKind.Ready),
                _ => (
                    "Przygotowanie",
                    DashboardStatusKind.Pending),
            };
        }
        else
        {
            (label, status) = _isRecoveryJournalClean switch
            {
                true => ("Gotowe", DashboardStatusKind.Ready),
                false => (
                    "Wymaga przywrócenia",
                    DashboardStatusKind.Error),
                null => (
                    "Sprawdzanie…",
                    DashboardStatusKind.Pending),
            };
        }

        DashboardRecoveryStateText.Text = label;
        ApplyDashboardStatusIndicator(
            DashboardRecoveryStatusIndicator,
            DashboardRecoveryStatusGlyph,
            status);
    }

    private void SetDashboardFrameRateStatus(
        string label,
        DashboardStatusKind status)
    {
        DashboardFrameRateStateText.Text = label;
        DashboardFrameRateStateDot.Fill = new SolidColorBrush(
            GetDashboardStatusColor(status));
    }

    private static void ApplyDashboardStatusIndicator(
        Border indicator,
        FontIcon glyph,
        DashboardStatusKind status)
    {
        WindowsColor color = GetDashboardStatusColor(status);
        indicator.Background = new SolidColorBrush(
            WindowsColor.FromArgb(36, color.R, color.G, color.B));
        glyph.Foreground = new SolidColorBrush(color);
        glyph.Glyph = status switch
        {
            DashboardStatusKind.Ready => "\uE73E",
            DashboardStatusKind.Warning => "\uE7BA",
            DashboardStatusKind.Error => "\uE711",
            _ => "\uE895",
        };
    }

    private static WindowsColor GetDashboardStatusColor(
        DashboardStatusKind status) =>
        status switch
        {
            DashboardStatusKind.Ready =>
                WindowsColor.FromArgb(255, 61, 203, 112),
            DashboardStatusKind.Warning =>
                WindowsColor.FromArgb(255, 255, 165, 31),
            DashboardStatusKind.Error =>
                WindowsColor.FromArgb(255, 255, 95, 98),
            _ => WindowsColor.FromArgb(255, 0, 215, 226),
        };

    private static void ShowInfo(
        InfoBar infoBar,
        InfoBarSeverity severity,
        string title,
        string message)
    {
        infoBar.Severity = severity;
        infoBar.Title = title;
        infoBar.Message = message;
        infoBar.IsOpen = true;

        if (severity == InfoBarSeverity.Success)
        {
            TacticalAudioService.Instance.PlaySuccess();
        }
        else if (severity is InfoBarSeverity.Warning or InfoBarSeverity.Error)
        {
            TacticalAudioService.Instance.PlayWarning();
        }
    }

    private static string RedactSid(string sid)
    {
        int lastSeparator = sid.LastIndexOf('-');
        return lastSeparator > 0
            ? $"{sid[..Math.Min(7, lastSeparator)]}…{sid[lastSeparator..]}"
            : "SID ukryty";
    }

    private static bool IsExpectedUiFailure(Exception exception) =>
        exception is
            IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or DbException
            or COMException
            or CryptographicException
            or TimeoutException;

    private static bool IsSessionConnectionFailure(Exception exception) =>
        exception is IOException or TimeoutException;

    private enum DashboardStatusKind
    {
        Pending,
        Ready,
        Warning,
        Error,
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Dispose();
    }

    private void UpdateGamingDiagnostics()
    {
        try
        {
            WindowsGamingDiagnosticReport report =
                WindowsGamingEnvironmentDiagnostic.Evaluate();
            string hagsDesc = report.HagsEnabled switch
            {
                true => "Włączone (Optymalnie)",
                false => "Wyłączone",
                null => "Niedostępne / Nieobsługiwane",
            };
            string gameModeDesc = report.GameModeEnabled
                ? "Włączony (Optymalnie)"
                : "Wyłączony";
            string dvrDesc = report.BackgroundRecordingEnabled
                ? "Włączone (Zużywa zasoby GPU)"
                : "Wyłączone (Optymalnie)";

            DiagnosticsGamingEnvironmentText.Text =
                $"Planowanie GPU (HAGS): {hagsDesc}\n"
                + $"Tryb gry Windows (Game Mode): {gameModeDesc}\n"
                + $"Nagrywanie w tle (Xbox Game DVR): {dvrDesc}";

            DiagnosticsGamingRecommendationsText.Text = string.Join(
                "\n\n",
                report.Recommendations.Select(
                    r => $"• {r.Title}\n  {r.Description}"));
        }
        catch (Exception exception)
        {
            DiagnosticsGamingEnvironmentText.Text =
                $"Błąd odczytu konfiguracji gamingowej: {exception.Message}";
            DiagnosticsGamingRecommendationsText.Text = string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _sessionPollTimer.Stop();
        _sessionPollTimer.Tick -= OnSessionPollTick;
        _overlaySettingsSaveTimer.Stop();
        _overlaySettingsSaveTimer.Tick -= OnOverlaySettingsSaveTick;
        _overlayPollTimer.Stop();
        _overlayPollTimer.Tick -= OnOverlayPollTick;
        _ = _foregroundGameWatcher?.DisposeAsync().AsTask();
        _foregroundGameWatcher = null;
        _lifetime.Cancel();
        _performanceOverlay?.Dispose();
        _performanceOverlay = null;
        _activationServer.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.OpenRequested -= OnTrayOpenRequested;
            _trayIcon.ExitRequested -= OnTrayExitRequested;
            _trayIcon.Dispose();
        }

        _sessions.Dispose();
        _updates.Dispose();
        _systemOptimizer.Dispose();
        _optiScaler.Dispose();
        _userDataStore.Dispose();
        _externalLaunchGate.Dispose();
        _lifetime.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

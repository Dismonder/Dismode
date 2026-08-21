using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameShift.Contracts.Protocol;
using GameShift.Core.Activation;
using GameShift.Core.Product;
using GameShift.Core.Profiles;
using GameShift.Core.Updates;
using GameShift.Data.Journal;
using GameShift.Data.Storage;
using GameShift.Data.UserData;
using GameShift.UI.Services;
using GameShift.UI.ViewModels;
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
    private readonly LocalSystemMetricsSampler _systemMetrics = new();
    private readonly UiActivationServer _activationServer;
    private readonly TrayIconService? _trayIcon;
    private readonly SemaphoreSlim _externalLaunchGate = new(1, 1);
    private readonly GameShiftLaunchOptions _launchOptions;
    private readonly string _userSid;
    private readonly ImageSource? _dashboardFallbackArtwork;
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
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
    private bool _isOverlaySettingsSaveRunning;
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
        ProfilesList.ItemsSource = _profiles;
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
        _ = DispatcherQueue.TryEnqueue(Close);

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
        RoutedEventArgs args) =>
        await RefreshDiagnosticsAsync(_lifetime.Token);

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
        ApplyOverlaySettingsFromControls(scheduleSave: true);

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

    private void OnDashboardGameRailSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_isSynchronizingProfileSelection)
        {
            return;
        }

        _isSynchronizingProfileSelection = true;
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
                    cancellationToken);
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
                    _lifetime.Token);
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

            switch (result.State)
            {
                case UpdateCheckState.NotDue:
                    UpdateActivitySpinner.IsActive = false;
                    UpdateActivitySpinner.Visibility = Visibility.Collapsed;
                    return;
                case UpdateCheckState.UpToDate:
                    _availableUpdate = null;
                    _stagedInstallerPath = null;
                    UpdateActivitySpinner.IsActive = false;
                    UpdateActivitySpinner.Visibility = Visibility.Collapsed;
                    ApplyNoAvailableUpdate();
                    if (manual)
                    {
                        ShowInfo(
                            UpdateInfoBar,
                            InfoBarSeverity.Success,
                            "GameShift jest aktualny",
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

        await _updates.VerifyInstallerAsync(
            manifest,
            _stagedInstallerPath,
            cancellationToken);

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

        await _updates.VerifyInstallerAsync(
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
                _lifetime.Token);

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
        if (session.FramesPerSecond is double framesPerSecond)
        {
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
            SetDashboardFrameRateStatus(
                session.State == "Active" ? "Oczekiwanie na klatki" : stateLabel,
                session.State == "Active"
                    ? DashboardStatusKind.Warning
                    : DashboardStatusKind.Pending);
            ActiveFpsText.Text = "—";
            ActiveFrameTimeText.Text = "— ms";
        }

        ActiveFrameRateStatusText.Text =
            (string.IsNullOrWhiteSpace(session.FrameRateStatus)
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
            FpsOverlayToggleSwitch.IsOn = preferences.IsEnabled;
            OverlayOpacitySlider.Value = preferences.OpacityPercent;
            OverlaySizeSlider.Value = preferences.ScalePercent;
            OverlayCornerSelector.SelectedIndex =
                GetOverlayCornerIndex(preferences.Corner);
            OverlayStyleSelector.SelectedIndex =
                GetOverlayStyleIndex(preferences.Style);
            OverlayThemeSelector.SelectedIndex =
                GetOverlayThemeIndex(preferences.Theme);
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
        if (FpsOverlayToggleSwitch is null
            || OverlayOpacitySlider is null
            || OverlaySizeSlider is null
            || OverlayCornerSelector is null
            || OverlayStyleSelector is null
            || OverlayThemeSelector is null
            || OverlayOpacityValueText is null
            || OverlaySizeValueText is null)
        {
            return;
        }

        int opacityPercent = GetOverlayOpacityPercent();
        int scalePercent = GetOverlayScalePercent();
        OverlayOpacityValueText.Text = $"{opacityPercent}%";
        OverlaySizeValueText.Text = $"{scalePercent}%";
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
            GetOverlayOpacityPercent(),
            GetOverlayScalePercent(),
            GetSelectedOverlayCorner(),
            GetSelectedOverlayStyle(),
            GetSelectedOverlayTheme(),
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

    private PerformanceOverlayStyle GetSelectedOverlayStyle() =>
        (OverlayStyleSelector?.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() switch
        {
            nameof(PerformanceOverlayStyle.CompactBar) =>
                PerformanceOverlayStyle.CompactBar,
            nameof(PerformanceOverlayStyle.MinimalText) =>
                PerformanceOverlayStyle.MinimalText,
            _ => PerformanceOverlayStyle.FullDeck,
        };

    private static int GetOverlayStyleIndex(
        PerformanceOverlayStyle style) =>
        style switch
        {
            PerformanceOverlayStyle.CompactBar => 1,
            PerformanceOverlayStyle.MinimalText => 2,
            _ => 0,
        };

    private PerformanceOverlayTheme GetSelectedOverlayTheme() =>
        (OverlayThemeSelector?.SelectedItem as ComboBoxItem)?
            .Tag?.ToString() switch
        {
            nameof(PerformanceOverlayTheme.MatrixGreen) =>
                PerformanceOverlayTheme.MatrixGreen,
            nameof(PerformanceOverlayTheme.ToxicGreen) =>
                PerformanceOverlayTheme.ToxicGreen,
            nameof(PerformanceOverlayTheme.ApexAmber) =>
                PerformanceOverlayTheme.ApexAmber,
            nameof(PerformanceOverlayTheme.CrimsonRed) =>
                PerformanceOverlayTheme.CrimsonRed,
            nameof(PerformanceOverlayTheme.PureWhite) =>
                PerformanceOverlayTheme.PureWhite,
            nameof(PerformanceOverlayTheme.StealthPurple) =>
                PerformanceOverlayTheme.StealthPurple,
            _ => PerformanceOverlayTheme.CyberNeon,
        };

    private static int GetOverlayThemeIndex(
        PerformanceOverlayTheme theme) =>
        theme switch
        {
            PerformanceOverlayTheme.MatrixGreen => 1,
            PerformanceOverlayTheme.ToxicGreen => 2,
            PerformanceOverlayTheme.ApexAmber => 3,
            PerformanceOverlayTheme.CrimsonRed => 4,
            PerformanceOverlayTheme.PureWhite => 5,
            PerformanceOverlayTheme.StealthPurple => 6,
            _ => 0,
        };

    private void UpdatePerformanceOverlay()
    {
        if (FpsOverlayToggleSwitch.IsOn is false
            || _activeSession is null)
        {
            HidePerformanceOverlay();
            return;
        }

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

    private void HidePerformanceOverlay() =>
        _performanceOverlay?.SetRequestedVisibility(false);

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

    private void ResetFrameRateDisplay()
    {
        _dashboardFrameSessionId = null;
        _dashboardFrameTimes.Clear();
        DashboardFrameTimeGraph.Points.Clear();
        ActiveFpsText.Text = "—";
        ActiveFrameTimeText.Text = "— ms";
        ActiveFrameRateStatusText.Text =
            "PresentMon uruchomi się automatycznie razem z grą.";
        SetDashboardFrameRateStatus(
            "Oczekuje",
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
        IReadOnlyList<ManualGameProfile> profiles =
            await _userDataStore.ListAsync(cancellationToken);
        IReadOnlyList<GameMetadata> metadata =
            await _userDataStore.ListMetadataAsync(cancellationToken);
        Dictionary<Core.Domain.Identifiers.GameProfileId, GameMetadata>
            metadataByProfile = metadata.ToDictionary(item => item.ProfileId);
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

        await Task.WhenAll(profileItems.Select(async item =>
        {
            item.ArtworkSource = await LocalArtworkImageLoader.LoadAsync(
                item.ArtworkPath,
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
        DashboardHeroImage.Source = profile?.ArtworkSource
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
        GlobalProgress.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        _userDataStore.Dispose();
        _externalLaunchGate.Dispose();
        _lifetime.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

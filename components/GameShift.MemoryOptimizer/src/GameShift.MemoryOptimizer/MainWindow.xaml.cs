using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GameShift.MemoryOptimizer.Core;
using GameShift.MemoryOptimizer.Core.Ipc;
using GameShift.MemoryOptimizer.Core.Models;
using GameShift.MemoryOptimizer.Presentation;
using GameShift.MemoryOptimizer.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WindowsColor = Windows.UI.Color;

namespace GameShift.MemoryOptimizer;

public sealed partial class MainWindow : Window
{
    private const uint SystemParametersGetHighContrast = 0x0042;
    private const uint HighContrastEnabled = 0x00000001;
    private const int SystemColorWindow = 5;
    private const int SystemColorWindowText = 8;

    private readonly MemoryOptimizerClient _client = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly MemorySettingsDraft _settingsDraft = new();
    private readonly MemoryRefreshPolicy _refreshPolicy = new();
    private readonly MemoryOperationGate _operationGate = new();
    private readonly MemoryRamTrend _ramTrend = new();
    private readonly MemoryFeedbackState _feedbackState = new();
    private readonly List<ProcessRow> _processRows = [];
    private readonly HashSet<string> _draftExclusions =
        new(StringComparer.OrdinalIgnoreCase);
    private MemoryOptimizerStatus? _status;
    private Guid? _lastObservedOperationId;
    private bool _statusInitialized;
    private bool _loadingSettings = true;
    private bool _uiReady;
    private bool _serviceAvailable;
    private bool _allowClose;
    private bool _pauseInProgress;
    private bool _dialogOpen;
    private bool _advancedUnlocked;
    private bool _globalWorkingSetAccepted;
    private bool _isCompactMode;
    private bool _isAlwaysOnTop;
    private bool _windowPresentationInitialized;
    private bool _adjustingWindow;
    private int _preferredMinimumWidth;
    private int _preferredMinimumHeight;
    private int _settingsRevision;

    private MemoryOptimizerSettings? PersistedSettings =>
        _settingsDraft.IsInitialized ? _settingsDraft.Baseline : null;

    public MainWindow()
    {
        InitializeComponent();
        Title = MemoryOptimizerProduct.DisplayName;
        AttributionText.Text =
            "Derived from Windows Memory Cleaner 3.0.8 © Igor Mundstein • " +
            $"GPL-3.0-only • GameShift {MemoryOptimizerProduct.Version}";
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBarDragRegion);
        SystemBackdrop = new MicaBackdrop();
        ConfigureWindowIcon();
        ConfigureTitleBar();
        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            AppWindow.Closing -= AppWindow_Closing;
            AppWindow.Changed -= AppWindow_Changed;
        };
        _uiReady = true;
        _loadingSettings = false;
        ShowPage("overview");
        UpdateOperationState();
    }

    public event EventHandler<TrayStatusEventArgs>? TrayStatusChanged;

    public event EventHandler<MemorySettingsEventArgs>? SettingsApplied;

    public event EventHandler<MemoryNotificationEventArgs>?
        TrayNotificationRequested;

    internal nint WindowHandle { get; }

    internal MemoryOptimizerStatus? CurrentTrayStatus => _serviceAvailable && _status is { } status
        ? status with { IsOptimizationRunning = status.IsOptimizationRunning || _operationGate.IsRunning }
        : null;

    internal bool CanOptimizeFromTray => OptimizeButton.IsEnabled;

    internal bool CanPauseFromTray => PauseButton.IsEnabled;

    internal bool StartMinimizedSetting =>
        _status?.Settings.StartMinimized ?? true;

    internal bool StartWithWindowsSetting =>
        _status?.Settings.StartWithWindows ?? true;

    internal async Task InitializeAsync()
    {
        await RefreshAsync(loadSettings: true).ConfigureAwait(true);
        _refreshTimer.Start();
    }

    internal async Task OptimizeNowAsync()
    {
        if (!_serviceAvailable ||
            !_operationGate.TryBegin(_status?.IsOptimizationRunning ?? false))
        {
            return;
        }

        UpdateOperationState();
        SetServiceMessage(
            "Optymalizacja w toku",
            "Usługa sprawdza aktywną grę i pozostałe blokady bezpieczeństwa.",
            "MemoryWarningBrush");
        try
        {
            MemoryArea areas = PersistedSettings?.ManualAreas ??
                MemoryOptimizerSettings.BasicAreas;
            OptimizationResult result = await _client.SendAsync<
                OptimizePayload,
                OptimizationResult>(
                MemoryOptimizerCommand.Optimize,
                new(OptimizationTrigger.Manual, areas),
                CancellationToken.None).ConfigureAwait(true);
            RenderLastResult(result);
            _refreshPolicy.InvalidateHistory();
            await RefreshAsync(loadSettings: false).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            ShowError(exception.Message);
        }
        finally
        {
            _operationGate.End();
            UpdateOperationState();
        }
    }

    internal async Task TogglePauseAsync()
    {
        if (_pauseInProgress || _settingsDraft.IsSaving || !_serviceAvailable)
        {
            return;
        }

        _pauseInProgress = true;
        _settingsRevision++;
        UpdateOperationState();
        try
        {
            bool pause = !(_status?.IsPaused ?? false);
            MemoryOptimizerSettings settings = await _client.SendAsync<
                object,
                MemoryOptimizerSettings>(
                pause
                    ? MemoryOptimizerCommand.PauseAutomation
                    : MemoryOptimizerCommand.ResumeAutomation,
                new { },
                CancellationToken.None).ConfigureAwait(true);
            _settingsDraft.Receive(settings);
            _settingsRevision++;
            SetDirty(_settingsDraft.HasChanges);
            await RefreshAsync(loadSettings: false).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            ShowError(exception.Message);
        }
        finally
        {
            _pauseInProgress = false;
            UpdateOperationState();
        }
    }

    internal void ShowFromTray()
    {
        _refreshPolicy.IsVisible = true;
        _ = ShowWindow(WindowHandle, 9);
        _ = SetForegroundWindow(WindowHandle);
        _ = RefreshAsync(loadSettings: false);
    }

    internal void HideToTray()
    {
        _refreshPolicy.IsVisible = false;
        _ = ShowWindow(WindowHandle, 0);
    }

    internal void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    internal async Task AuthorizeTrayShutdownAsync()
    {
        _ = await _client.SendAsync<object, ShutdownTrayReply>(
            MemoryOptimizerCommand.ShutdownTray,
            new { },
            CancellationToken.None).ConfigureAwait(true);
    }

    internal async Task<bool> ConfirmExitAsync()
    {
        if (!_settingsDraft.IsInitialized || !_settingsDraft.HasChanges)
        {
            return true;
        }

        ShowFromTray();
        ContentDialog confirmation = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Niezapisane zmiany",
            Content =
                "Ustawienia w tym oknie nie zostały jeszcze zapisane w usłudze. " +
                "Możesz je zapisać, odrzucić albo wrócić do programu.",
            PrimaryButtonText = "Zapisz i zamknij",
            SecondaryButtonText = "Odrzuć i zamknij",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult? result = await ShowDialogAsync(confirmation)
            .ConfigureAwait(true);
        if (result == ContentDialogResult.Secondary)
        {
            _settingsDraft.Discard();
            ApplyDraftToControls();
            return true;
        }

        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        try
        {
            await SaveSettingsAsync().ConfigureAwait(true);
            return !_settingsDraft.HasChanges;
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private async Task RefreshAsync(bool loadSettings)
    {
        if (!_refreshPolicy.TryBeginStatus())
        {
            return;
        }

        int settingsRevision = _settingsRevision;
        try
        {
            MemoryOptimizerStatus status = await _client.SendAsync<
                object,
                MemoryOptimizerStatus>(
                MemoryOptimizerCommand.GetStatus,
                new { },
                CancellationToken.None).ConfigureAwait(true);
            _status = status;
            _serviceAvailable = true;
            if (_feedbackState.ClearRecoveredServiceError())
            {
                GlobalInfoBar.IsOpen = false;
                if (status.LastResult is null)
                {
                    ClearCompactFeedback();
                }
            }

            bool acceptSettings = settingsRevision == _settingsRevision &&
                !_settingsDraft.IsSaving && !_pauseInProgress;
            bool settingsChanged = false;
            if (acceptSettings)
            {
                settingsChanged = _settingsDraft.Receive(status.Settings);
            }

            RenderStatus(status);
            if (!_dialogOpen && acceptSettings && (loadSettings || settingsChanged))
            {
                ApplyDraftToControls();
                ApplyCompactMode(
                    status.Settings.CompactMode,
                    status.Settings.CompactAlwaysOnTop,
                    resizeWindow: loadSettings);
                SettingsApplied?.Invoke(this, new(status.Settings));
            }

            SetDirty(_settingsDraft.HasChanges);
            if (_refreshPolicy.ShouldRefreshProcesses)
            {
                RefreshProcesses();
            }

            if (_refreshPolicy.NeedsHistory)
            {
                await RefreshHistoryAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            _serviceAvailable = false;
            ShowError(
                "Usługa Memory Optimizer jest niedostępna. " +
                exception.Message,
                isServiceConnectionError: true);
            TitleBarStatusText.Text = "Usługa offline";
            ServiceStatusDot.Fill = ResourceBrush("MemoryDangerBrush");
            ServiceStatusTitle.Text = "Brak połączenia z usługą";
            ServiceStatusMessage.Text =
                "Sprawdź stan GameShift Memory Service i spróbuj ponownie.";
            CompactStatusText.Text = "USŁUGA OFFLINE";
        }
        finally
        {
            _refreshPolicy.EndStatus();
            UpdateOperationState();
        }
    }

    private void RenderStatus(MemoryOptimizerStatus status)
    {
        MemorySnapshot memory = status.Memory;
        UsedRamText.Text = FormatBytes(memory.UsedPhysicalBytes);
        AvailableRamText.Text = FormatBytes(memory.AvailablePhysicalBytes);
        TotalRamText.Text = FormatBytes(memory.TotalPhysicalBytes);
        CommitRamText.Text = MemoryUiFormatting.FormatCommit(memory, CultureInfo.CurrentCulture);
        RamPercentText.Text = $"{memory.MemoryLoadPercent}%";
        RamUsageRing.Value = memory.MemoryLoadPercent;
        RamUsageSummaryText.Text =
            $"{FormatBytes(memory.UsedPhysicalBytes)} z " +
            FormatBytes(memory.TotalPhysicalBytes);
        CompactRamPercentText.Text = $"{memory.MemoryLoadPercent}%";
        CompactRamDetailsText.Text =
            $"{FormatBytes(memory.UsedPhysicalBytes)} / " +
            FormatBytes(memory.TotalPhysicalBytes);
        CompactRamProgress.Value = memory.MemoryLoadPercent;

        string statusTitle = status.IsPaused
            ? "Automat jest wstrzymany"
            : status.IsOptimizationRunning
                ? "Optymalizacja w toku"
                : "Usługa działa prawidłowo";
        string statusMessage = status.IsPaused
            ? "Ręczna optymalizacja nadal jest dostępna."
            : "Usługa działa niezależnie od okna i blokuje operacje podczas gry.";
        SetServiceMessage(
            statusTitle,
            statusMessage,
            status.IsPaused ? "MemoryWarningBrush" : "MemorySuccessBrush");
        ServiceVersionText.Text = $"Usługa {status.ServiceVersion}";
        TitleBarStatusText.Text = status.IsPaused
            ? "Wstrzymany"
            : $"RAM {memory.MemoryLoadPercent}%";
        CompactStatusText.Text = status.IsOptimizationRunning || _operationGate.IsRunning
            ? "OPTYMALIZACJA W TOKU"
            : status.IsPaused ? "AUTOMAT WSTRZYMANY"
            : status.Settings.AutomationEnabled ? "AUTOMAT DZIAŁA" : "TYLKO TRYB RĘCZNY";
        PauseButton.Content = status.IsPaused
            ? "Wznów automat"
            : "Wstrzymaj automat";
        CompactPauseButton.Content = status.IsPaused ? "Wznów" : "Wstrzymaj";
        AutomationSummaryTitle.Text = status.Settings.AutomationEnabled
            ? status.IsPaused
                ? "Włączona, ale wstrzymana"
                : "Automatyzacja jest aktywna"
            : "Automatyzacja jest wyłączona";
        AutomationSummaryDetails.Text = status.Settings.AutomationEnabled
            ? $"Próg {status.Settings.AvailableMemoryThresholdPercent}% dostępnego RAM • " +
                $"cooldown {status.Settings.CooldownMinutes} min"
            : "Ręczna optymalizacja pozostaje dostępna.";
        OptimizationBlockReasonText.Text = status.LastResult?.State == OptimizationState.Blocked
            ? "Ostatnia próba zablokowana: " + status.LastResult.Message
            : "Usługa sprawdza grę i zabezpieczenia przed każdą operacją. Używa zapisanego profilu.";

        AppendRamSample(memory);
        if (status.LastResult is not null)
        {
            RenderLastResult(status.LastResult);
            if (_statusInitialized &&
                _lastObservedOperationId != status.LastResult.OperationId)
            {
                _refreshPolicy.InvalidateHistory();
                if (status.Settings.NotificationsEnabled)
                {
                    TrayNotificationRequested?.Invoke(
                        this,
                        new(
                            "GameShift Memory Optimizer",
                            $"{FormatOptimizationState(status.LastResult.State)}: " +
                                $"zmiana dostępnego RAM " +
                                FormatSignedBytes(
                                    status.LastResult.AvailableMemoryDeltaBytes)));
                }
            }

            _lastObservedOperationId = status.LastResult.OperationId;
        }

        _statusInitialized = true;
        UpdateOperationState();
    }

    private void ApplyDraftToControls()
    {
        MemoryOptimizerSettings settings = _settingsDraft.Current;
        _loadingSettings = true;
        try
        {
            AutomationSwitch.IsOn = settings.AutomationEnabled;
            RamThresholdBox.Value = settings.AvailableMemoryThresholdPercent;
            CpuThresholdBox.Value = settings.MaximumIdleCpuPercent;
            IdleMinutesBox.Value = settings.IdleCpuMinutes;
            CooldownBox.Value = settings.CooldownMinutes;
            ScheduleSwitch.IsOn = settings.ScheduleEnabled;
            ScheduleIntervalBox.Value = settings.ScheduleIntervalMinutes;
            PollIntervalBox.Value = settings.PollIntervalSeconds;
            NotificationsCheckBox.IsChecked = settings.NotificationsEnabled;
            StartWithWindowsCheckBox.IsChecked = settings.StartWithWindows;
            StartMinimizedCheckBox.IsChecked = settings.StartMinimized;
            CompactModeCheckBox.IsChecked = settings.CompactMode;
            CompactPinButton.IsChecked = settings.CompactAlwaysOnTop;
            HotkeyTextBox.Text = settings.Hotkey;
            _advancedUnlocked = settings.AdvancedModeEnabled;
            _globalWorkingSetAccepted = settings.GlobalWorkingSet;
            WorkingSetScopeCombo.SelectedIndex = settings.GlobalWorkingSet
                ? 1
                : 0;
            _draftExclusions.Clear();
            _draftExclusions.UnionWith(settings.ExcludedProcesses);
            ApplySelectedAreas(settings.ManualAreas, automatic: false);
            ApplySelectedAreas(settings.AutomaticAreas, automatic: true);
            RenderAdvancedAccess();
            RenderExclusions();
            ScheduleIntervalBox.IsEnabled = ScheduleSwitch.IsOn;
        }
        finally
        {
            _loadingSettings = false;
        }

        SetDirty(_settingsDraft.HasChanges);
    }

    private void ApplySelectedAreas(MemoryArea areas, bool automatic)
    {
        AreaControls controls = automatic
            ? new(
                AutomaticWorkingSetArea,
                AutomaticLowStandbyArea,
                AutomaticStandbyArea,
                AutomaticSystemFileCacheArea,
                AutomaticModifiedPageArea,
                AutomaticCombinedPageArea,
                AutomaticRegistryCacheArea,
                AutomaticModifiedFileCacheArea)
            : new(
                ManualWorkingSetArea,
                ManualLowStandbyArea,
                ManualStandbyArea,
                ManualSystemFileCacheArea,
                ManualModifiedPageArea,
                ManualCombinedPageArea,
                ManualRegistryCacheArea,
                ManualModifiedFileCacheArea);
        controls.WorkingSet.IsChecked = HasArea(areas, MemoryArea.WorkingSet);
        controls.LowStandby.IsChecked = HasArea(
            areas,
            MemoryArea.StandbyListLowPriority);
        controls.Standby.IsChecked = HasArea(areas, MemoryArea.StandbyList);
        controls.SystemFileCache.IsChecked = HasArea(
            areas,
            MemoryArea.SystemFileCache);
        controls.ModifiedPage.IsChecked = HasArea(
            areas,
            MemoryArea.ModifiedPageList);
        controls.CombinedPage.IsChecked = HasArea(
            areas,
            MemoryArea.CombinedPageList);
        controls.RegistryCache.IsChecked = HasArea(
            areas,
            MemoryArea.RegistryCache);
        controls.ModifiedFileCache.IsChecked = HasArea(
            areas,
            MemoryArea.ModifiedFileCache);
    }

    private MemoryArea BuildSelectedAreas(bool automatic)
    {
        AreaControls controls = automatic
            ? new(
                AutomaticWorkingSetArea,
                AutomaticLowStandbyArea,
                AutomaticStandbyArea,
                AutomaticSystemFileCacheArea,
                AutomaticModifiedPageArea,
                AutomaticCombinedPageArea,
                AutomaticRegistryCacheArea,
                AutomaticModifiedFileCacheArea)
            : new(
                ManualWorkingSetArea,
                ManualLowStandbyArea,
                ManualStandbyArea,
                ManualSystemFileCacheArea,
                ManualModifiedPageArea,
                ManualCombinedPageArea,
                ManualRegistryCacheArea,
                ManualModifiedFileCacheArea);
        MemoryArea areas = MemoryArea.None;
        AddIfChecked(controls.WorkingSet, MemoryArea.WorkingSet, ref areas);
        AddIfChecked(
            controls.LowStandby,
            MemoryArea.StandbyListLowPriority,
            ref areas);
        AddIfChecked(controls.Standby, MemoryArea.StandbyList, ref areas);
        AddIfChecked(
            controls.SystemFileCache,
            MemoryArea.SystemFileCache,
            ref areas);
        AddIfChecked(
            controls.ModifiedPage,
            MemoryArea.ModifiedPageList,
            ref areas);
        AddIfChecked(
            controls.CombinedPage,
            MemoryArea.CombinedPageList,
            ref areas);
        AddIfChecked(
            controls.RegistryCache,
            MemoryArea.RegistryCache,
            ref areas);
        AddIfChecked(
            controls.ModifiedFileCache,
            MemoryArea.ModifiedFileCache,
            ref areas);
        return areas == MemoryArea.None
            ? MemoryOptimizerSettings.BasicAreas
            : areas;
    }

    private MemoryOptimizerSettings BuildSettingsFromControls() => new()
    {
        AutomationEnabled = AutomationSwitch.IsOn,
        AutomationPaused = _status?.IsPaused ?? false,
        AvailableMemoryThresholdPercent = GetInteger(RamThresholdBox, 20),
        MaximumIdleCpuPercent = GetInteger(CpuThresholdBox, 5),
        IdleCpuMinutes = GetInteger(IdleMinutesBox, 5),
        CooldownMinutes = GetInteger(CooldownBox, 30),
        PollIntervalSeconds = GetInteger(PollIntervalBox, 15),
        ScheduleEnabled = ScheduleSwitch.IsOn,
        ScheduleIntervalMinutes = GetInteger(ScheduleIntervalBox, 240),
        AdvancedModeEnabled = _advancedUnlocked,
        AdvancedWarningAccepted = _advancedUnlocked,
        GlobalWorkingSet = WorkingSetScopeCombo.SelectedIndex == 1,
        GlobalWorkingSetWarningAccepted = _globalWorkingSetAccepted,
        ManualAreas = BuildSelectedAreas(automatic: false),
        AutomaticAreas = BuildSelectedAreas(automatic: true),
        ExcludedProcesses = _draftExclusions
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        NotificationsEnabled = NotificationsCheckBox.IsChecked == true,
        StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
        StartMinimized = StartMinimizedCheckBox.IsChecked == true,
        Hotkey = HotkeyTextBox.Text,
        CompactMode = CompactModeCheckBox.IsChecked == true,
        CompactAlwaysOnTop = CompactPinButton.IsChecked == true,
    };

    private async Task SaveSettingsAsync()
    {
        if (_settingsDraft.IsSaving || _pauseInProgress || !_serviceAvailable)
        {
            return;
        }

        ValidateSettingsInputs();
        _settingsDraft.Edit(BuildSettingsFromControls());
        _settingsRevision++;
        SaveButton.IsEnabled = false;
        SaveProgressRing.IsActive = true;
        SaveProgressRing.Opacity = 1;
        DirtyBarMessage.Text = "Zapisywanie i walidowanie ustawień…";
        try
        {
            Task<bool> save = _settingsDraft.SaveAsync(SendSettingsAsync, CancellationToken.None);
            UpdateOperationState();
            if (await save.ConfigureAwait(true))
            {
                _settingsRevision++;
                ApplyDraftToControls();
                ApplyCompactMode(
                    _settingsDraft.Baseline.CompactMode,
                    _settingsDraft.Baseline.CompactAlwaysOnTop,
                    resizeWindow: false);
                SettingsApplied?.Invoke(this, new(_settingsDraft.Baseline));
                GlobalInfoBar.IsOpen = false;
            }

            await RefreshAsync(loadSettings: false).ConfigureAwait(true);
        }
        catch
        {
            DirtyBarMessage.Text =
                "Nie udało się zapisać. Szkic pozostał bez zmian.";
            throw;
        }
        finally
        {
            SaveProgressRing.IsActive = false;
            SaveProgressRing.Opacity = 0;
            SetDirty(_settingsDraft.HasChanges);
            UpdateOperationState();
        }
    }

    private Task<MemoryOptimizerSettings> SendSettingsAsync(
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken) =>
        _client.SendAsync<MemoryOptimizerSettings, MemoryOptimizerSettings>(
            MemoryOptimizerCommand.SaveSettings, settings, cancellationToken);

    private void ValidateSettingsInputs()
    {
        if (!NumberSettingsAreValid())
        {
            _settingsDraft.MarkInvalidInput();
            SetDirty(true);
            throw new InvalidOperationException(
                "Wpisz pełne liczby w dozwolonym zakresie. Puste pola nie zostaną zapisane.");
        }

        if (!HotkeyBinding.TryParse(HotkeyTextBox.Text, out _))
        {
            _settingsDraft.MarkInvalidInput();
            SetDirty(true);
            throw new InvalidOperationException("Nieprawidłowy skrót. Przykład: Ctrl+Shift+M.");
        }
    }

    private bool SettingsInputsAreValid() =>
        NumberSettingsAreValid() && HotkeyBinding.TryParse(HotkeyTextBox.Text, out _);

    private bool NumberSettingsAreValid()
    {
        NumberBox[] numbers =
        [
            RamThresholdBox, CpuThresholdBox, IdleMinutesBox, CooldownBox,
            PollIntervalBox, ScheduleIntervalBox,
        ];
        return numbers.All(box => double.IsFinite(box.Value) &&
            box.Value == Math.Truncate(box.Value) &&
            box.Value >= box.Minimum && box.Value <= box.Maximum);
    }

    private async Task RefreshHistoryAsync()
    {
        if (!_refreshPolicy.TryBeginHistory())
        {
            return;
        }

        bool success = false;
        try
        {
            IReadOnlyList<OptimizationResult> history = await _client.SendAsync<
                HistoryPayload,
                IReadOnlyList<OptimizationResult>>(
                MemoryOptimizerCommand.GetHistory,
                new(100),
                CancellationToken.None).ConfigureAwait(true);
            RenderHistory(history);
            success = true;
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            ShowError("Nie udało się odczytać historii. " + exception.Message);
        }
        finally
        {
            _refreshPolicy.EndHistory(success);
        }
    }

    private void RenderLastResult(OptimizationResult result)
    {
        string state = FormatOptimizationState(result.State);
        string availableDelta = FormatSignedBytes(
            result.AvailableMemoryDeltaBytes);
        string details =
            $"{result.CompletedAtUtc.ToLocalTime():g} • {state} • " +
            $"dostępny RAM {availableDelta} • working sety " +
            $"{FormatBytes(result.WorkingSetBytesReleased)}. {result.Message}";
        LastResultText.Text = details;
        CompactFeedbackText.Text = result.State == OptimizationState.Blocked
            ? "Zablokowano: " + result.Message
            : $"{state} · dostępny RAM {availableDelta}";
        CompactFeedbackText.Foreground = ResourceBrush(
            result.State is OptimizationState.Blocked or OptimizationState.Failed
                ? "MemoryWarningBrush" : "MemoryMutedBrush");
        ToolTipService.SetToolTip(CompactFeedbackText, details);
        HistoryLastResultTitle.Text =
            $"{state} • {result.CompletedAtUtc.ToLocalTime():g}";
        HistoryLastResultDetails.Text = details;
    }

    private void RenderHistory(IReadOnlyList<OptimizationResult> history)
    {
        HistoryList.ItemsSource = history.Select(result => new HistoryRow(
            MemoryUiFormatting.FormatTimestamp(result.CompletedAtUtc),
            FormatTrigger(result.Trigger),
            $"{FormatOptimizationState(result.State)} • dostępny RAM " +
                $"{FormatSignedBytes(result.AvailableMemoryDeltaBytes)} • " +
                $"working sety {FormatBytes(result.WorkingSetBytesReleased)}",
            FormatHistoryDetails(result)))
            .ToArray();
        bool isEmpty = history.Count == 0;
        HistoryEmptyState.Visibility = isEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryList.Visibility = isEmpty
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void RefreshProcesses()
    {
        _processRows.Clear();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    _processRows.Add(new(
                        process.Id,
                        process.ProcessName,
                        process.WorkingSet64,
                        _draftExclusions.Contains(process.ProcessName)));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                    // A process can exit while the read-only list is refreshed.
                }
            }
        }

        RefreshProcessView();
    }

    private void RefreshProcessView()
    {
        if (!_uiReady)
        {
            return;
        }

        string query = ProcessSearchBox.Text.Trim();
        IEnumerable<ProcessRow> filtered = _processRows.Where(row =>
            query.Length == 0 ||
            row.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        filtered = ProcessSortCombo.SelectedIndex switch
        {
            1 => filtered.OrderBy(static row => row.Name, StringComparer.OrdinalIgnoreCase),
            2 => filtered.OrderByDescending(
                static row => row.Name,
                StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(static row => row.WorkingSetBytes),
        };
        ProcessRow[] rows = filtered.Take(200).ToArray();
        ProcessList.ItemsSource = rows;
        ProcessCountText.Text = $"{rows.Length} z {_processRows.Count}";
    }

    private void RenderExclusions()
    {
        ExclusionRow[] rows = _draftExclusions
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static name => new ExclusionRow(name))
            .ToArray();
        ExclusionList.ItemsSource = rows;
        ExclusionEmptyText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExclusionList.Visibility = rows.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void AppendRamSample(MemorySnapshot memory)
    {
        if (!_ramTrend.Add(memory))
        {
            return;
        }

        PointCollection points = [];
        IReadOnlyList<MemorySnapshot> samples = _ramTrend.Samples;
        if (samples.Count == 1)
        {
            double y = 86 - (samples[0].MemoryLoadPercent / 100d * 82);
            points.Add(new(590, y));
            points.Add(new(600, y));
        }
        else
        {
            foreach (MemorySnapshot sample in samples)
            {
                double x = 600 - (memory.CapturedAtUtc - sample.CapturedAtUtc).TotalSeconds * 10;
                double y = 86 - (sample.MemoryLoadPercent / 100d * 82);
                points.Add(new Point(x, y));
            }
        }

        RamTrendLine.Points = points;
    }

    private void RenderAdvancedAccess()
    {
        AdvancedLockedPanel.Visibility = _advancedUnlocked
            ? Visibility.Collapsed
            : Visibility.Visible;
        AdvancedUnlockedPanel.Visibility = _advancedUnlocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        AdvancedNavigationIcon.Glyph = _advancedUnlocked
            ? "\uE785"
            : "\uE72E";
        ToolTipService.SetToolTip(AdvancedNavigationItem,
            _advancedUnlocked ? "Zaawansowane: odblokowane" : "Zaawansowane: zablokowane");
        bool exclusionsEnabled = WorkingSetScopeCombo.SelectedIndex != 1;
        NewExclusionTextBox.IsEnabled = exclusionsEnabled;
        ExclusionList.IsEnabled = exclusionsEnabled;
        AddExclusionButton.IsEnabled = exclusionsEnabled;
        ExclusionsDisabledNotice.Visibility = exclusionsEnabled
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetDirty(bool isDirty)
    {
        DirtyBar.Opacity = isDirty ? 1 : 0;
        DirtyBar.IsHitTestVisible = isDirty;
        if (!isDirty)
        {
            DirtyBarMessage.Text =
                "Zmiany pozostaną lokalne, dopóki ich nie zapiszesz.";
        }

        CompactDraftNotice.Opacity = isDirty ? 1 : 0;
    }

    private void MarkDirty()
    {
        if (_uiReady && !_loadingSettings && _settingsDraft.IsInitialized)
        {
            _settingsRevision++;
            if (!SettingsInputsAreValid())
            {
                _settingsDraft.MarkInvalidInput();
                SetDirty(true);
                ScheduleIntervalBox.IsEnabled = ScheduleSwitch.IsOn;
                return;
            }

            _settingsDraft.Edit(BuildSettingsFromControls());
            SetDirty(_settingsDraft.HasChanges);
            ScheduleIntervalBox.IsEnabled = ScheduleSwitch.IsOn;
        }
    }

    private void ShowPage(string key)
    {
        _refreshPolicy.SelectPage(key);
        OverviewPage.Visibility = key == "overview"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationPage.Visibility = key == "automation"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProcessesPage.Visibility = key == "processes"
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryPage.Visibility = key == "history"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AdvancedPage.Visibility = key == "advanced"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void NavigateTo(string key)
    {
        NavigationViewItem? item = RootNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Tag?.ToString(),
                    key,
                    StringComparison.Ordinal));
        if (item is not null)
        {
            RootNavigation.SelectedItem = item;
        }
    }

    private void ApplyCompactMode(
        bool compact,
        bool alwaysOnTop,
        bool resizeWindow = false)
    {
        bool modeChanged = _isCompactMode != compact;
        bool alwaysOnTopChanged = _isAlwaysOnTop != alwaysOnTop;
        bool firstApply = !_windowPresentationInitialized;
        if (!firstApply && !modeChanged && !alwaysOnTopChanged && !resizeWindow)
        {
            return;
        }

        _isCompactMode = compact;
        _isAlwaysOnTop = alwaysOnTop;
        _refreshPolicy.IsCompact = compact;
        if (firstApply || modeChanged)
        {
            NormalShell.Visibility = compact
                ? Visibility.Collapsed
                : Visibility.Visible;
            CompactShell.Visibility = compact
                ? Visibility.Visible
                : Visibility.Collapsed;
            TitleBarName.Text = compact ? "Memory Optimizer" : "GameShift Memory Optimizer";
            TitleBarStatusBadge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            TitleBarRow.Height = new(compact ? 32 : 48);
            AppWindow.TitleBar.PreferredHeightOption = compact
                ? TitleBarHeightOption.Standard : TitleBarHeightOption.Tall;
        }

        if (firstApply || modeChanged || alwaysOnTopChanged)
        {
            CompactPinButton.IsChecked = alwaysOnTop;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                if (modeChanged && (resizeWindow || firstApply) &&
                    presenter.State != OverlappedPresenterState.Restored)
                {
                    presenter.Restore();
                }

                presenter.IsAlwaysOnTop = compact && alwaysOnTop;
                presenter.IsResizable = !compact;
                presenter.IsMaximizable = !compact;
            }
        }

        _windowPresentationInitialized = true;
        if (firstApply || modeChanged)
        {
            UpdateWindowConstraints();
            UpdateTitleBarInsets();
        }

        if (resizeWindow && (firstApply || modeChanged))
        {
            ResizeWindowLogical(
                compact ? 420 : 1180,
                compact ? 240 : 850);
        }
    }

    private void UpdateOperationState()
    {
        bool busy = _operationGate.IsRunning ||
            (_status?.IsOptimizationRunning ?? false);
        bool saving = _settingsDraft.IsSaving;
        bool interactive = !_dialogOpen;
        OptimizeButton.IsEnabled = _serviceAvailable && !busy && !saving && interactive;
        CompactOptimizeButton.IsEnabled = OptimizeButton.IsEnabled;
        PauseButton.IsEnabled = _serviceAvailable && !saving && !_pauseInProgress && interactive;
        CompactPauseButton.IsEnabled = PauseButton.IsEnabled;
        SaveButton.IsEnabled = _serviceAvailable && !saving && !_pauseInProgress && interactive;
        DiscardButton.IsEnabled = !saving && interactive;
        CompactPinButton.IsEnabled = _serviceAvailable && !saving && !_pauseInProgress && interactive;
        CompactModeButton.IsEnabled = _serviceAvailable && !saving && !_pauseInProgress && interactive;
        CompactExpandButton.IsEnabled = !saving && interactive;
        CompactBusyProgress.Opacity = busy || saving ? 1 : 0;
        OptimizeProgressRing.IsActive = busy;
        OptimizeProgressRing.Opacity = busy ? 1 : 0;
        TrayStatusChanged?.Invoke(
            this,
            new(_status?.Memory.MemoryLoadPercent ?? 0, _status?.IsPaused ?? false));
    }

    private void SetServiceMessage(
        string title,
        string message,
        string brushKey)
    {
        ServiceStatusTitle.Text = title;
        ServiceStatusMessage.Text = message;
        ServiceStatusDot.Fill = ResourceBrush(brushKey);
    }

    private void ShowError(string message, bool isServiceConnectionError = false)
    {
        _feedbackState.ShowError(message, isServiceConnectionError);
        GlobalInfoBar.Title = "Memory Optimizer";
        GlobalInfoBar.Message = message;
        GlobalInfoBar.Severity = InfoBarSeverity.Error;
        GlobalInfoBar.IsOpen = true;
        CompactFeedbackText.Text = message;
        CompactFeedbackText.Foreground = ResourceBrush("MemoryDangerBrush");
        ToolTipService.SetToolTip(CompactFeedbackText, message);
    }

    private void ClearCompactFeedback()
    {
        CompactFeedbackText.Text = string.Empty;
        CompactFeedbackText.Foreground = ResourceBrush("MemoryMutedBrush");
        ToolTipService.SetToolTip(CompactFeedbackText, null);
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        bool highContrast = IsHighContrastEnabled();
        WindowsColor background = highContrast
            ? GetSystemColor(SystemColorWindow)
            : WindowsColor.FromArgb(255, 9, 13, 18);
        WindowsColor foreground = highContrast
            ? GetSystemColor(SystemColorWindowText)
            : WindowsColor.FromArgb(255, 235, 243, 247);
        WindowsColor inactive = highContrast ? foreground
            : WindowsColor.FromArgb(255, 132, 148, 158);
        WindowsColor transparent = WindowsColor.FromArgb(0, 0, 0, 0);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = highContrast ? foreground
            : WindowsColor.FromArgb(255, 31, 43, 53);
        titleBar.ButtonHoverForegroundColor = highContrast ? background : foreground;
        titleBar.ButtonPressedBackgroundColor = highContrast ? foreground
            : WindowsColor.FromArgb(255, 43, 58, 70);
        titleBar.ButtonPressedForegroundColor = highContrast ? background : foreground;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }

    private void UpdateTitleBarInsets()
    {
        double scale = RootLayout.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WindowHandle) / 96d;
        TitleBarLeftInset.Width = new(AppWindow.TitleBar.LeftInset / scale);
        TitleBarRightInset.Width = new(AppWindow.TitleBar.RightInset / scale);
    }

    private void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_uiReady)
        {
            UpdateTitleBarInsets();
        }
    }

    private void ConfigureWindowIcon()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "MemoryOptimizer.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private void ResizeWindowLogical(int logicalWidth, int logicalHeight)
    {
        double scale = RootLayout.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WindowHandle) / 96d;
        int width = Math.Max(1, checked((int)Math.Round(logicalWidth * scale)));
        int height = Math.Max(1, checked((int)Math.Round(logicalHeight * scale)));
        DisplayArea area = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        RectInt32 workArea = area.WorkArea;
        width = Math.Min(width, workArea.Width);
        height = Math.Min(height, workArea.Height);
        int maximumX = workArea.X + Math.Max(0, workArea.Width - width);
        int maximumY = workArea.Y + Math.Max(0, workArea.Height - height);
        int x = Math.Clamp(AppWindow.Position.X, workArea.X, maximumX);
        int y = Math.Clamp(AppWindow.Position.Y, workArea.Y, maximumY);
        RectInt32 target = new(x, y, width, height);
        if (AppWindow.Position.X == target.X &&
            AppWindow.Position.Y == target.Y &&
            AppWindow.Size.Width == target.Width &&
            AppWindow.Size.Height == target.Height)
        {
            return;
        }

        _adjustingWindow = true;
        try
        {
            AppWindow.MoveAndResize(target);
        }
        finally
        {
            _adjustingWindow = false;
        }
    }

    private void UpdateWindowConstraints()
    {
        if (_adjustingWindow || AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        double scale = RootLayout.XamlRoot?.RasterizationScale ?? GetDpiForWindow(WindowHandle) / 96d;
        RectInt32 workArea = DisplayArea.GetFromWindowId(
            AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int minimumWidth = Math.Min(workArea.Width,
            checked((int)Math.Round((_isCompactMode ? 420 : 940) * scale)));
        int minimumHeight = Math.Min(workArea.Height,
            checked((int)Math.Round((_isCompactMode ? 240 : 640) * scale)));
        if (_preferredMinimumWidth != minimumWidth)
        {
            presenter.PreferredMinimumWidth = minimumWidth;
            _preferredMinimumWidth = minimumWidth;
        }

        if (_preferredMinimumHeight != minimumHeight)
        {
            presenter.PreferredMinimumHeight = minimumHeight;
            _preferredMinimumHeight = minimumHeight;
        }
    }

    private async void RootNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (!_uiReady ||
            args.SelectedItemContainer?.Tag?.ToString() is not string key)
        {
            return;
        }

        ShowPage(key);
        if (_refreshPolicy.ShouldRefreshProcesses)
        {
            RefreshProcesses();
        }
        else if (_refreshPolicy.NeedsHistory)
        {
            await RefreshHistoryAsync().ConfigureAwait(true);
        }
    }

    private void RootLayout_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureTitleBar();
        RootLayout.ActualThemeChanged += (_, _) => ConfigureTitleBar();
        if (PersistedSettings is { } settings)
        {
            ApplyCompactMode(
                settings.CompactMode,
                settings.CompactAlwaysOnTop,
                resizeWindow: true);
        }
        else
        {
            ApplyCompactMode(false, false, resizeWindow: true);
        }

        UpdateTitleBarInsets();
        UpdateWindowConstraints();
    }

    private void AppWindow_Changed(
        AppWindow sender,
        AppWindowChangedEventArgs args)
    {
        if (_uiReady && args.DidSizeChange && !_adjustingWindow)
        {
            UpdateWindowConstraints();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(loadSettings: false).ConfigureAwait(true);

    private async void RefreshHistoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _refreshPolicy.InvalidateHistory();
        await RefreshHistoryAsync().ConfigureAwait(true);
    }

    private async void OptimizeButton_Click(object sender, RoutedEventArgs e) =>
        await OptimizeNowAsync().ConfigureAwait(true);

    private async void PauseButton_Click(object sender, RoutedEventArgs e) =>
        await TogglePauseAsync().ConfigureAwait(true);

    private void OpenAutomationButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo("automation");

    private void OpenHistoryButton_Click(object sender, RoutedEventArgs e) =>
        NavigateTo("history");

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            ShowError(exception.Message);
        }
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsDraft.IsInitialized && !_settingsDraft.IsSaving)
        {
            _settingsDraft.Discard();
            ApplyDraftToControls();
        }
    }

    private async void CompactButton_Click(object sender, RoutedEventArgs e) =>
        await SaveWindowPreferencesAsync(true, _settingsDraft.Baseline.CompactAlwaysOnTop)
            .ConfigureAwait(true);

    private async void CompactExpandButton_Click(
        object sender,
        RoutedEventArgs e)
        => await SaveWindowPreferencesAsync(false, _settingsDraft.Baseline.CompactAlwaysOnTop)
            .ConfigureAwait(true);

    private async void CompactPinButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        await SaveWindowPreferencesAsync(_isCompactMode, CompactPinButton.IsChecked == true)
            .ConfigureAwait(true);
    }

    private async Task SaveWindowPreferencesAsync(bool compact, bool alwaysOnTop)
    {
        if (!_serviceAvailable || _settingsDraft.IsSaving || _pauseInProgress)
        {
            // Expanding must remain possible even while the service is offline.
            if (!compact)
            {
                ApplyCompactMode(false, alwaysOnTop, resizeWindow: true);
            }

            return;
        }

        _settingsRevision++;
        try
        {
            Task<bool> save = _settingsDraft.SaveWindowPreferencesAsync(
                compact, alwaysOnTop, SendSettingsAsync, CancellationToken.None);
            UpdateOperationState();
            if (await save.ConfigureAwait(true))
            {
                _settingsRevision++;
                ApplyDraftToControls();
                ApplyCompactMode(compact, alwaysOnTop, resizeWindow: true);
                SettingsApplied?.Invoke(this, new(_settingsDraft.Baseline));
            }
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            CompactPinButton.IsChecked = _settingsDraft.Baseline.CompactAlwaysOnTop;
            ShowError("Nie udało się zapisać ustawień okna. " + exception.Message);
            if (!compact)
            {
                ApplyCompactMode(
                    false,
                    _settingsDraft.Baseline.CompactAlwaysOnTop,
                    resizeWindow: true);
            }
        }
        finally
        {
            UpdateOperationState();
        }
    }

    private async void UnlockAdvancedButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ContentDialog warning = new()
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = "Odblokować agresywne operacje pamięci?",
            Content =
                "Globalne listy pamięci Windows mogą zostać ponownie zapełnione, " +
                "co czasowo zwiększa użycie dysku i opóźnienia. Te funkcje nie " +
                "gwarantują wzrostu FPS. Ochrona aktywnej gry pozostanie włączona.",
            PrimaryButtonText = "Rozumiem ryzyko i odblokowuję",
            CloseButtonText = "Anuluj",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(warning).ConfigureAwait(true) !=
            ContentDialogResult.Primary)
        {
            return;
        }

        _advancedUnlocked = true;
        RenderAdvancedAccess();
        MarkDirty();
    }

    private void LockAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        _loadingSettings = true;
        try
        {
            _advancedUnlocked = false;
            _globalWorkingSetAccepted = false;
            WorkingSetScopeCombo.SelectedIndex = 0;
            ApplySelectedAreas(
                MemoryOptimizerSettings.BasicAreas,
                automatic: false);
            ApplySelectedAreas(
                MemoryOptimizerSettings.BasicAreas,
                automatic: true);
            RenderAdvancedAccess();
        }
        finally
        {
            _loadingSettings = false;
        }

        MarkDirty();
    }

    private async void WorkingSetScopeCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        if (WorkingSetScopeCombo.SelectedIndex == 1)
        {
            ContentDialog warning = new()
            {
                XamlRoot = RootLayout.XamlRoot,
                Title = "Globalne working sety",
                Content =
                    "Ta opcja obejmuje cały system, wyłącza wykluczenia i może " +
                    "spowodować ponowne doczytywanie danych z dysku.",
                PrimaryButtonText = "Użyj globalnego zakresu",
                CloseButtonText = "Wróć",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(warning).ConfigureAwait(true) !=
                ContentDialogResult.Primary)
            {
                _loadingSettings = true;
                WorkingSetScopeCombo.SelectedIndex = 0;
                _loadingSettings = false;
                return;
            }

            _globalWorkingSetAccepted = true;
        }
        else
        {
            _globalWorkingSetAccepted = false;
        }

        RenderAdvancedAccess();
        MarkDirty();
    }

    private void ManualStandbyArea_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            _loadingSettings = true;
            ManualLowStandbyArea.IsChecked = false;
            _loadingSettings = false;
            MarkDirty();
        }
    }

    private void ManualLowStandbyArea_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            _loadingSettings = true;
            ManualStandbyArea.IsChecked = false;
            _loadingSettings = false;
            MarkDirty();
        }
    }

    private void AutomaticStandbyArea_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            _loadingSettings = true;
            AutomaticLowStandbyArea.IsChecked = false;
            _loadingSettings = false;
            MarkDirty();
        }
    }

    private void AutomaticLowStandbyArea_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loadingSettings)
        {
            _loadingSettings = true;
            AutomaticStandbyArea.IsChecked = false;
            _loadingSettings = false;
            MarkDirty();
        }
    }

    private void SettingsControl_Changed(object sender, RoutedEventArgs e) =>
        MarkDirty();

    private void NumberSetting_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        MarkDirty();

    private void TextSetting_Changed(
        object sender,
        TextChangedEventArgs e) =>
        MarkDirty();

    private void ProcessSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        RefreshProcessView();

    private void ProcessSortCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        RefreshProcessView();

    private void AddExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkingSetScopeCombo.SelectedIndex == 1 || !_settingsDraft.IsInitialized)
        {
            return;
        }

        string name = NormalizeProcessName(NewExclusionTextBox.Text);
        if (name.Length == 0 || _draftExclusions.Count >= 256)
        {
            return;
        }

        if (_draftExclusions.Add(name))
        {
            NewExclusionTextBox.Text = string.Empty;
            RenderExclusions();
            RefreshProcesses();
            MarkDirty();
        }
    }

    private void RemoveExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } &&
            _draftExclusions.Remove(name))
        {
            RenderExclusions();
            RefreshProcesses();
            MarkDirty();
        }
    }

    private void ProcessExclusionButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkingSetScopeCombo.SelectedIndex == 1)
        {
            ShowError("Globalny zakres working setów nie obsługuje wykluczeń.");
            return;
        }

        if (sender is not Button { Tag: string processName })
        {
            return;
        }

        if (!_draftExclusions.Remove(processName))
        {
            if (_draftExclusions.Count >= 256)
            {
                return;
            }

            _draftExclusions.Add(processName);
        }

        RenderExclusions();
        RefreshProcesses();
        MarkDirty();
    }

    private void RefreshTimer_Tick(object? sender, object e) =>
        _ = RefreshAsync(loadSettings: false);

    private async Task<ContentDialogResult?> ShowDialogAsync(
        ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            return null;
        }

        _dialogOpen = true;
        UpdateOperationState();
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
            UpdateOperationState();
        }
    }

    private void AppWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private static bool IsExpectedClientFailure(Exception exception) =>
        exception is IOException or InvalidOperationException or
            TimeoutException or OperationCanceledException;

    private static bool HasArea(MemoryArea value, MemoryArea area) =>
        (value & area) != 0;

    private static void AddIfChecked(
        CheckBox checkBox,
        MemoryArea area,
        ref MemoryArea value)
    {
        if (checkBox.IsChecked == true)
        {
            value |= area;
        }
    }

    private static int GetInteger(NumberBox box, int fallback) =>
        !double.IsFinite(box.Value) || box.Value < int.MinValue || box.Value > int.MaxValue
            ? fallback
            : checked((int)Math.Round(box.Value));

    private static string NormalizeProcessName(string? value)
    {
        string fileName = Path.GetFileNameWithoutExtension(
            (value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(fileName)
            ? string.Empty
            : fileName;
    }

    private static string FormatBytes(ulong bytes) =>
        MemoryUiFormatting.FormatBytes(bytes, CultureInfo.CurrentCulture);

    private static string FormatSignedBytes(long bytes)
    {
        string sign = bytes > 0 ? "+" : string.Empty;
        return $"{sign}{bytes / (1024d * 1024):0} MB";
    }

    private static string FormatOptimizationState(OptimizationState state) =>
        state switch
        {
            OptimizationState.Completed => "Zakończono",
            OptimizationState.PartiallyCompleted => "Częściowo zakończono",
            OptimizationState.Blocked => "Zablokowano",
            OptimizationState.AlreadyRunning => "Operacja już trwa",
            OptimizationState.Cancelled => "Anulowano",
            OptimizationState.Failed => "Niepowodzenie",
            _ => state.ToString(),
        };

    private static string FormatTrigger(OptimizationTrigger trigger) =>
        trigger switch
        {
            OptimizationTrigger.Manual => "Ręcznie",
            OptimizationTrigger.LowMemory => "Niski poziom RAM",
            OptimizationTrigger.Schedule => "Harmonogram",
            OptimizationTrigger.Hotkey => "Skrót klawiszowy",
            _ => trigger.ToString(),
        };

    private static string FormatAreas(MemoryArea areas)
    {
        string[] names = Enum.GetValues<MemoryArea>()
            .Where(area => area != MemoryArea.None && HasArea(areas, area))
            .Select(area => area switch
            {
                MemoryArea.CombinedPageList => "Combined page list",
                MemoryArea.ModifiedFileCache => "Modified file cache",
                MemoryArea.ModifiedPageList => "Modified page list",
                MemoryArea.RegistryCache => "Registry cache",
                MemoryArea.StandbyList => "Standby list",
                MemoryArea.StandbyListLowPriority =>
                    "Standby list (niski priorytet)",
                MemoryArea.SystemFileCache => "System file cache",
                MemoryArea.WorkingSet => "Working sety",
                _ => area.ToString(),
            })
            .ToArray();
        return names.Length == 0 ? "Brak obszarów" : string.Join(" • ", names);
    }

    private static string FormatHistoryDetails(OptimizationResult result)
    {
        string failures = string.Join(" • ", result.AreaResults
            .Where(static item => !item.Succeeded)
            .Select(item => $"{FormatAreas(item.Area)}: {item.Message}" +
                (item.Win32Error.HasValue ? $" (kod {item.Win32Error.Value})" : string.Empty)));
        double seconds = Math.Max(0, (result.CompletedAtUtc - result.StartedAtUtc).TotalSeconds);
        return $"{FormatAreas(result.RequestedAreas)} · {seconds:0.0} s\n" +
            result.Message + (failures.Length > 0 ? "\n" + failures : string.Empty);
    }

    private static Brush ResourceBrush(string key) =>
        Application.Current.Resources[key] as Brush ??
        new SolidColorBrush(WindowsColor.FromArgb(255, 143, 161, 174));

    private static bool IsHighContrastEnabled()
    {
        HighContrastParameters parameters = new()
        {
            Size = checked((uint)Marshal.SizeOf<HighContrastParameters>()),
        };
        return SystemParametersInfo(
                SystemParametersGetHighContrast,
                parameters.Size,
                ref parameters,
                0) &&
            (parameters.Flags & HighContrastEnabled) != 0;
    }

    private static WindowsColor GetSystemColor(int index)
    {
        uint color = GetSysColor(index);
        return WindowsColor.FromArgb(
            255,
            unchecked((byte)color),
            unchecked((byte)(color >> 8)),
            unchecked((byte)(color >> 16)));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "SystemParametersInfoW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrastParameters parameters,
        uint update);

    [LibraryImport("user32.dll")]
    private static partial uint GetSysColor(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastParameters
    {
        internal uint Size;
        internal uint Flags;
        internal nint DefaultScheme;
    }

    private sealed record OptimizePayload(
        OptimizationTrigger Trigger,
        MemoryArea Areas);

    private sealed record HistoryPayload(int MaximumCount);

    private sealed record ShutdownTrayReply(bool ShutdownTray);

    private sealed record HistoryRow(
        string Title,
        string Trigger,
        string Details,
        string Areas);

    private sealed record ExclusionRow(string Name);

    private sealed record ProcessRow(
        int ProcessId,
        string Name,
        long WorkingSetBytes,
        bool IsExcluded)
    {
        public string WorkingSet => FormatBytes(
            checked((ulong)Math.Max(WorkingSetBytes, 0)));

        public string ProcessIdLabel => $"PID {ProcessId}";

        public string ActionLabel => IsExcluded ? "Przywróć" : "Wyklucz";
    }

    private readonly record struct AreaControls(
        CheckBox WorkingSet,
        CheckBox LowStandby,
        CheckBox Standby,
        CheckBox SystemFileCache,
        CheckBox ModifiedPage,
        CheckBox CombinedPage,
        CheckBox RegistryCache,
        CheckBox ModifiedFileCache);
}

public sealed class MemorySettingsEventArgs : EventArgs
{
    public MemorySettingsEventArgs(MemoryOptimizerSettings settings)
    {
        Settings = settings;
    }

    public MemoryOptimizerSettings Settings { get; }
}

public sealed class MemoryNotificationEventArgs : EventArgs
{
    public MemoryNotificationEventArgs(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }

    public string Message { get; }
}

public sealed class TrayStatusEventArgs : EventArgs
{
    public TrayStatusEventArgs(uint memoryLoadPercent, bool isPaused)
    {
        MemoryLoadPercent = memoryLoadPercent;
        IsPaused = isPaused;
    }

    public uint MemoryLoadPercent { get; }

    public bool IsPaused { get; }
}

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Dismode.Contracts.Grpc;
using Dismode.Contracts.SystemOptimization;
using Dismode.Core.Product;
using Dismode.Core.SystemOptimization;
using Dismode.SystemOptimizer.Services;
using Dismode.Windows.Processes;
using Dismode.Windows.SystemOptimization;
using Grpc.Core;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WindowsColor = Windows.UI.Color;

namespace Dismode.SystemOptimizer;

[SuppressMessage(
    "Design",
    "CA1001",
    Justification = "The WinUI Window lifecycle disposes the IPC client from the Closed event.")]
public sealed partial class MainWindow : Window
{
    internal static string WindowTitle =>
        $"Dismode System Optimizer {ProductInformation.CurrentVersion}";

    private static readonly TimeSpan StabilizationDuration =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MeasurementDuration =
        TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions ProfileJsonOptions = new(
        JsonSerializerDefaults.Web);

    private readonly string _callerSid;
    private readonly SystemOptimizerClient _client;
    private readonly ActiveGameLeaseReader _activeGameLeaseReader;
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Dictionary<string, SystemTweakDefinitionMessage>
        _catalogDefinitions = new(StringComparer.Ordinal);
    private SystemOptimizerStatusReply? _status;
    private SystemExperimentReply? _activeExperiment;
    private ActiveGameLease? _activeGameLease;
    private string? _hardwareFingerprintHash;
    private CancellationTokenSource? _benchmarkCancellation;
    private bool _refreshInProgress;
    private bool _operationInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Title = WindowTitle;
        TitleBarVersionText.Text = ProductInformation.CurrentVersion;
        _callerSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException(
                "Nie można odczytać SID bieżącego użytkownika.");
        _client = new(_callerSid);
        _activeGameLeaseReader = ActiveGameLeaseReader.CreateForUser(
            _callerSid);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        SystemBackdrop = new MicaBackdrop();
        ConfigureWindowIcon();
        ConfigureTitleBar();
        ConfigureInitialWindowSize();
        _refreshTimer.Interval = TimeSpan.FromSeconds(10);
        _refreshTimer.Tick += RefreshTimer_Tick;
        Closed += MainWindow_Closed;
        ShowPage("overview");
    }

    private async void RootLayout_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarInsets();
        UpdateMinimumWindowSize();
        RootLayout.ActualThemeChanged += RootLayout_ActualThemeChanged;
        await RefreshAsync().ConfigureAwait(true);
        _refreshTimer.Start();
    }

    private void RootLayout_ActualThemeChanged(
        FrameworkElement sender,
        object args) => ConfigureTitleBar();

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _benchmarkCancellation?.Cancel();
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        RootLayout.ActualThemeChanged -= RootLayout_ActualThemeChanged;
        if (!_operationInProgress)
        {
            _client.Dispose();
        }
    }

    private async void RefreshTimer_Tick(object? sender, object e) =>
        await RefreshAsync().ConfigureAwait(true);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private async void ConsentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null || _operationInProgress || _status.IsReadOnly)
        {
            return;
        }

        await RunOperationAsync(
                token => _client.SetConsentAsync(
                    !_status.HasUserConsent,
                    token))
            .ConfigureAwait(true);
    }

    private async void RestoreAllButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_status is null || _operationInProgress || _status.IsReadOnly)
        {
            return;
        }

        await RunOperationAsync(_client.RestoreAllAsync)
            .ConfigureAwait(true);
    }

    private async void DetectActiveGameButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RefreshActiveGameLeaseAsync().ConfigureAwait(true);

    private async void PrepareExperimentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (SafeTweakComboBox.SelectedItem is not SafeTweakOption option)
        {
            return;
        }

        _ = await PrepareExperimentAsync(
                option.Definition,
                dangerousConfirmation: null)
            .ConfigureAwait(true);
    }

    private async void PrepareDangerousExperimentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DangerousTweakComboBox.SelectedItem is not SafeTweakOption option
            || !StringComparer.Ordinal.Equals(
                DangerousConfirmationTextBox.Text,
                SystemTweakSelectionValidator.DangerousConfirmationText))
        {
            SetServiceState(
                "Wymagane świadome potwierdzenie",
                "Wpisz dokładnie ROZUMIEM RYZYKO dla tej jednej pozycji.",
                "OptimizerWarningBrush");
            return;
        }

        bool prepared = await PrepareExperimentAsync(
                option.Definition,
                DangerousConfirmationTextBox.Text)
            .ConfigureAwait(true);
        if (prepared)
        {
            DangerousConfirmationTextBox.Text = string.Empty;
            ShowPage("experiments");
        }
    }

    private void DangerousTweakComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) => UpdateOperationSurface();

    private void DangerousConfirmationTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e) => UpdateOperationSurface();

    private async Task<bool> PrepareExperimentAsync(
        SystemTweakDefinitionMessage definition,
        string? dangerousConfirmation)
    {
        if (_operationInProgress
            || _status is null
            || _status.IsReadOnly
            || !_status.HasUserConsent
            || _activeExperiment is not null
            || string.IsNullOrWhiteSpace(_hardwareFingerprintHash))
        {
            return false;
        }

        ActiveGameLease? lease = await RefreshActiveGameLeaseAsync()
            .ConfigureAwait(true);
        if (lease is null)
        {
            return false;
        }

        bool prepared = false;
        _operationInProgress = true;
        UpdateOperationSurface();
        try
        {
            _activeExperiment = await _client.PrepareExperimentAsync(
                    lease.GameId,
                    lease.ExecutableSha256,
                    _hardwareFingerprintHash,
                    definition,
                    dangerousConfirmation,
                    CancellationToken.None)
                .ConfigureAwait(true);
            ResetBenchmarkDecisionSurface();
            ExperimentStepText.Text = _activeExperiment.Message;
            SetServiceState(
                "Eksperyment przygotowany",
                _activeExperiment.Message,
                "OptimizerSuccessBrush");
            prepared = true;
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            SetServiceState(
                "Nie można przygotować eksperymentu",
                exception.Message,
                "OptimizerWarningBrush");
        }
        finally
        {
            _operationInProgress = false;
            UpdateOperationSurface();
        }

        await RefreshAsync().ConfigureAwait(true);
        return prepared;
    }

    private async void RunBenchmarkButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationInProgress
            || _activeExperiment is null
            || _activeExperiment.State
                != ExperimentStateValue.ExperimentStateWaitingForScene)
        {
            return;
        }

        ActiveGameLease? lease = await RefreshActiveGameLeaseAsync()
            .ConfigureAwait(true);
        if (lease is null
            || !StringComparer.Ordinal.Equals(
                lease.GameId,
                _activeExperiment.GameProfileId)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                lease.ExecutableSha256,
                _activeExperiment.GameExecutableSha256))
        {
            SetServiceState(
                "Aktywna gra nie odpowiada eksperymentowi",
                "Wznów dokładnie tę samą grę przez Dismode i wykryj ją ponownie.",
                "OptimizerWarningBrush");
            return;
        }

        _benchmarkCancellation = new();
        _operationInProgress = true;
        UpdateOperationSurface();
        try
        {
            BenchmarkDecisionReply decision = await RunBenchmarkCaptureAsync(
                    lease,
                    _activeExperiment,
                    _benchmarkCancellation.Token)
                .ConfigureAwait(true);
            _activeExperiment = decision.Experiment;
            RenderBenchmarkDecision(decision);
            ExperimentStepText.Text = decision.Explanation;
            SetServiceState(
                "Wariant zarejestrowany",
                decision.Explanation,
                decision.Verdict == (int)BenchmarkVerdict.CandidateWins
                    ? "OptimizerSuccessBrush"
                    : "OptimizerWarningBrush");
        }
        catch (OperationCanceledException)
        {
            await TryCancelAndRestoreExperimentAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            SetServiceState(
                "Pomiar nie został ukończony",
                exception.Message,
                "OptimizerWarningBrush");
            await TryCancelAndRestoreExperimentAsync().ConfigureAwait(true);
        }
        finally
        {
            _benchmarkCancellation.Dispose();
            _benchmarkCancellation = null;
            _operationInProgress = false;
            ExperimentProgressBar.Opacity = 0;
            ExperimentProgressBar.Value = 0;
            UpdateOperationSurface();
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async void KeepResultButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationInProgress
            || _activeExperiment is null
            || _activeExperiment.State
                != ExperimentStateValue.ExperimentStateCompleted)
        {
            return;
        }

        await RunExperimentActionAsync(
                ExperimentControlAction.KeepAsGameProfile,
                "Zapisano profil dla gry",
                clearCompletedExperiment: true)
            .ConfigureAwait(true);
    }

    private async void KeepGlobalResultButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_operationInProgress
            || _activeExperiment is null
            || _activeExperiment.State
                != ExperimentStateValue.ExperimentStateCompleted)
        {
            return;
        }

        await RunExperimentActionAsync(
                ExperimentControlAction.KeepGlobally,
                "Zapisano profil globalny",
                clearCompletedExperiment: true)
            .ConfigureAwait(true);
    }

    private async void CancelExperimentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_benchmarkCancellation is not null)
        {
            _benchmarkCancellation.Cancel();
            return;
        }

        if (!_operationInProgress && _activeExperiment is not null)
        {
            await RunExperimentActionAsync(
                    ExperimentControlAction.CancelAndRestore,
                    "Eksperyment anulowany")
                .ConfigureAwait(true);
        }
    }

    private async Task<BenchmarkDecisionReply> RunBenchmarkCaptureAsync(
        ActiveGameLease lease,
        SystemExperimentReply experiment,
        CancellationToken cancellationToken)
    {
        BenchmarkVariantValue variant = GetExpectedVariant(experiment);
        experiment = await _client.AdvanceExperimentAsync(
                experiment.ExperimentId,
                ExperimentControlAction.BeginStabilization,
                cancellationToken)
            .ConfigureAwait(true);
        _activeExperiment = experiment;
        ExperimentProgressBar.Opacity = 1;
        ExperimentProgressBar.Value = 0;
        ExperimentStepText.Text =
            $"Stabilizacja wariantu {FormatVariant(variant)} — pozostań w tej samej scenie.";

        await using PresentMonFrameRateProvider provider =
            PresentMonFrameRateProvider.CreateBenchmarkProvider();
        for (int second = 0;
            second < checked((int)StabilizationDuration.TotalSeconds);
            second++)
        {
            FrameRateSample sample = await provider.SampleAsync(
                    [lease.ProcessId],
                    cancellationToken)
                .ConfigureAwait(true);
            ThrowIfFrameCaptureUnavailable(sample);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(true);
            ExperimentProgressBar.Value = second + 1;
        }

        experiment = await _client.AdvanceExperimentAsync(
                experiment.ExperimentId,
                ExperimentControlAction.BeginCapture,
                cancellationToken)
            .ConfigureAwait(true);
        _activeExperiment = experiment;
        ExperimentStepText.Text =
            $"Pomiar wariantu {FormatVariant(variant)} — nie zmieniaj sceny przez 90 sekund.";

        using Process gameProcess = Process.GetProcessById(lease.ProcessId);
        DateTimeOffset captureStartedAtUtc = DateTimeOffset.UtcNow;
        TimeSpan processorTimeBefore = gameProcess.TotalProcessorTime;
        long? usedRamBefore =
            SystemMemoryUsageReader.TryReadUsedPhysicalMemoryBytes();
        Task<PresentMonBenchmarkCapture> captureTask = provider
            .CaptureBenchmarkAsync(
                lease.ProcessId,
                MeasurementDuration,
                cancellationToken)
            .AsTask();
        int measurementSeconds = checked((int)MeasurementDuration.TotalSeconds);
        for (int second = 0; second < measurementSeconds; second++)
        {
            Task completed = await Task.WhenAny(
                    captureTask,
                    Task.Delay(TimeSpan.FromSeconds(1), cancellationToken))
                .ConfigureAwait(true);
            if (ReferenceEquals(completed, captureTask))
            {
                break;
            }

            ExperimentProgressBar.Value =
                StabilizationDuration.TotalSeconds + second + 1;
        }

        PresentMonBenchmarkCapture capture = await captureTask
            .ConfigureAwait(true);
        gameProcess.Refresh();
        TimeSpan elapsed = DateTimeOffset.UtcNow - captureStartedAtUtc;
        double cpuPercent = elapsed > TimeSpan.Zero
            ? Math.Clamp(
                (gameProcess.TotalProcessorTime - processorTimeBefore)
                    .TotalMilliseconds
                / elapsed.TotalMilliseconds
                / Environment.ProcessorCount
                * 100d,
                0d,
                100d)
            : 0d;
        long? averageUsedRam = AverageNonNegativeBytes(
            usedRamBefore,
            SystemMemoryUsageReader.TryReadUsedPhysicalMemoryBytes());
        return await _client.SubmitCaptureAsync(
                experiment.ExperimentId,
                variant,
                captureStartedAtUtc,
                StabilizationDuration,
                MeasurementDuration,
                capture.FrameTimesMilliseconds,
                cpuPercent,
                capture.AverageGpuBusyPercent,
                averageUsedRam,
                cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<SystemOptimizerOperationReply>> operation)
    {
        SystemOptimizerOperationReply? operationResult = null;
        _operationInProgress = true;
        UpdateOperationSurface();
        try
        {
            operationResult = await operation(
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            SetDisconnectedState(exception.Message);
        }
        finally
        {
            _operationInProgress = false;
            UpdateOperationSurface();
        }

        if (operationResult is not null)
        {
            await RefreshAsync().ConfigureAwait(true);
            if (_status is not null)
            {
                SetServiceState(
                    operationResult.Succeeded
                        ? "Operacja zakończona"
                        : "Operacja zablokowana",
                    operationResult.Message,
                    operationResult.Succeeded
                        ? "OptimizerSuccessBrush"
                        : operationResult.RecoveryRequired
                            ? "OptimizerDangerBrush"
                            : "OptimizerWarningBrush");
            }
        }
    }

    private async Task<ActiveGameLease?> RefreshActiveGameLeaseAsync()
    {
        ActiveGameLease? lease = await _activeGameLeaseReader.TryReadAsync(
                _callerSid,
                CancellationToken.None)
            .ConfigureAwait(true);
        _activeGameLease = lease;
        ActiveGameLeaseText.Text = lease is null
            ? "Brak aktualnej, zweryfikowanej dzierżawy gry. Uruchom grę przez Dismode."
            : $"{lease.DisplayName} • PID {lease.ProcessId} • "
                + $"SHA-256 {lease.ExecutableSha256[..12]}…";
        UpdateOperationSurface();
        return lease;
    }

    private async Task RunExperimentActionAsync(
        ExperimentControlAction action,
        string successTitle,
        bool clearCompletedExperiment = false)
    {
        if (_activeExperiment is null)
        {
            return;
        }

        _operationInProgress = true;
        UpdateOperationSurface();
        try
        {
            _activeExperiment = await _client.AdvanceExperimentAsync(
                    _activeExperiment.ExperimentId,
                    action,
                    CancellationToken.None)
                .ConfigureAwait(true);
            ExperimentStepText.Text = _activeExperiment.Message;
            SetServiceState(
                successTitle,
                _activeExperiment.Message,
                "OptimizerSuccessBrush");
            if (clearCompletedExperiment)
            {
                _activeExperiment = null;
            }
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            SetServiceState(
                "Operacja eksperymentu została zablokowana",
                exception.Message,
                "OptimizerWarningBrush");
        }
        finally
        {
            _operationInProgress = false;
            UpdateOperationSurface();
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task TryCancelAndRestoreExperimentAsync()
    {
        if (_activeExperiment is null)
        {
            return;
        }

        try
        {
            _activeExperiment = await _client.AdvanceExperimentAsync(
                    _activeExperiment.ExperimentId,
                    ExperimentControlAction.CancelAndRestore,
                    CancellationToken.None)
                .ConfigureAwait(true);
            ExperimentStepText.Text = _activeExperiment.Message;
            SetServiceState(
                "Pomiar anulowany i przywrócony",
                _activeExperiment.Message,
                "OptimizerSuccessBrush");
        }
        catch (Exception exception) when (IsExpectedOperationFailure(exception))
        {
            SetServiceState(
                "Wymagane ręczne sprawdzenie recovery",
                exception.Message,
                "OptimizerDangerBrush");
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress || _operationInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        UpdateOperationSurface();
        try
        {
            Task<SystemOptimizerStatusReply> statusTask =
                _client.GetStatusAsync(CancellationToken.None);
            Task<SystemOptimizerHardwareReply> hardwareTask =
                _client.GetHardwareAsync(CancellationToken.None);
            Task<SystemTweakCatalogReply> catalogTask =
                _client.GetCatalogAsync(CancellationToken.None);
            Task<SystemOptimizationHistoryReply> historyTask =
                _client.GetHistoryAsync(100, CancellationToken.None);
            Task<SystemOptimizationProfilesReply> profilesTask =
                _client.GetProfilesAsync(CancellationToken.None);
            await Task.WhenAll(
                    statusTask,
                    hardwareTask,
                    catalogTask,
                    historyTask,
                    profilesTask)
                .ConfigureAwait(true);

            _status = await statusTask.ConfigureAwait(true);
            RenderStatus(_status);
            RenderHardware(await hardwareTask.ConfigureAwait(true));
            RenderCatalog(await catalogTask.ConfigureAwait(true));
            RenderHistory(await historyTask.ConfigureAwait(true));
            RenderProfiles(await profilesTask.ConfigureAwait(true));
        }
        catch (Exception exception) when (IsExpectedClientFailure(exception))
        {
            _status = null;
            SetDisconnectedState(exception.Message);
        }
        finally
        {
            _refreshInProgress = false;
            UpdateOperationSurface();
        }
    }

    private void RenderStatus(SystemOptimizerStatusReply status)
    {
        string mode = status.IsReadOnly ? "Tylko odczyt" : "Pełny dostęp";
        TitleBarModeText.Text = mode;
        SetServiceState(
            status.IsReadOnly
                ? "Usługa działa — tryb tylko do odczytu"
                : "Usługa działa prawidłowo",
            status.Message,
            status.IsReadOnly
                ? "OptimizerWarningBrush"
                : "OptimizerSuccessBrush");
        OverviewStateText.Text = status.HasUserConsent
            ? "Bezpieczny tryb jest aktywny"
            : "Oczekiwanie na świadomą zgodę";
        OverviewDetailText.Text = status.Message;
        ConsentButton.Content = status.HasUserConsent
            ? "Wyłącz zgodę"
            : "Włącz bezpieczny tryb";
        ConsentButton.IsEnabled = !status.IsReadOnly;
        RestoreAllButton.IsEnabled = !status.IsReadOnly;

        RecoveryStatusMessage recovery = status.Recovery;
        RecoveryMetricText.Text = recovery.IsJournalClean
            ? "Czysty"
            : recovery.HasConflicts
                ? "Konflikt"
                : "Wymaga restore";
        RecoveryMetricDetail.Text = string.IsNullOrWhiteSpace(recovery.Message)
            ? "Journal maszyny jest gotowy."
            : recovery.Message;
        GameProfileMetricText.Text = ShortIdentifier(
            status.ActiveGameProfileId,
            "Brak");
        ExperimentMetricText.Text = ShortIdentifier(
            status.ActiveExperimentId,
            "Brak");
        ActiveGameProfileText.Text = string.IsNullOrWhiteSpace(
            status.ActiveGameProfileId)
                ? "Brak aktywnego profilu gry."
                : $"Aktywny profil gry: {status.ActiveGameProfileId}";
        ActiveExperimentText.Text = string.IsNullOrWhiteSpace(
            status.ActiveExperimentId)
                ? "Brak aktywnego eksperymentu."
                : $"Aktywny eksperyment: {status.ActiveExperimentId}";
        GlobalProfileText.Text = string.IsNullOrWhiteSpace(
            status.ActiveGlobalProfileId)
                ? "Brak aktywnego profilu globalnego."
                : $"Aktywny profil globalny: {status.ActiveGlobalProfileId}";
        if (status.ActiveExperiment is not null)
        {
            _activeExperiment = status.ActiveExperiment;
        }
        else if (_activeExperiment?.State
            != ExperimentStateValue.ExperimentStateCompleted)
        {
            _activeExperiment = null;
        }

        UpdateOperationSurface();
    }

    private void RenderHardware(SystemOptimizerHardwareReply hardware)
    {
        _hardwareFingerprintHash = hardware.FingerprintHash;
        WindowsBuildText.Text = hardware.WindowsBuild;
        ArchitectureText.Text = hardware.CpuArchitecture;
        CpuText.Text = string.Join(
            ' ',
            new[] { hardware.CpuVendor, hardware.CpuModelFamily }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        PhysicalMemoryText.Text = FormatBytes(hardware.PhysicalMemoryBytes);
        GraphicsText.Text = hardware.GraphicsAdapters.Count == 0
            ? "Nie wykryto adaptera"
            : string.Join(Environment.NewLine, hardware.GraphicsAdapters);
        NetworkText.Text = hardware.NetworkAdapterClasses.Count == 0
            ? "Nie wykryto adaptera"
            : string.Join(Environment.NewLine, hardware.NetworkAdapterClasses);
        FingerprintText.Text = hardware.FingerprintHash;
    }

    private void RenderCatalog(SystemTweakCatalogReply catalog)
    {
        string? selectedTweakId =
            (SafeTweakComboBox.SelectedItem as SafeTweakOption)
                ?.Definition.Id;
        string? selectedDangerousTweakId =
            (DangerousTweakComboBox.SelectedItem as SafeTweakOption)
                ?.Definition.Id;
        SafeTweakComboBox.Items.Clear();
        DangerousTweakComboBox.Items.Clear();
        _catalogDefinitions.Clear();
        LaboratoryCatalogList.Children.Clear();
        DangerCatalogList.Children.Clear();
        foreach (SystemTweakDefinitionMessage definition in
            catalog.Definitions.OrderBy(item => item.Category)
                .ThenBy(item => item.DisplayName))
        {
            _catalogDefinitions[definition.Id] = definition;
            bool danger = definition.Risk is
                    SystemTweakRiskValue.SystemTweakRiskDangerous
                    or SystemTweakRiskValue.SystemTweakRiskBlocked
                || definition.Availability ==
                    SystemTweakAvailabilityValue.SystemTweakAvailabilityBlockedByPolicy;
            (danger ? DangerCatalogList : LaboratoryCatalogList)
                .Children.Add(CreateCatalogCard(definition));
            if (definition.Availability ==
                    SystemTweakAvailabilityValue.SystemTweakAvailabilitySupported
                && definition.Risk ==
                    SystemTweakRiskValue.SystemTweakRiskSafe
                && definition.Scope ==
                    SystemTweakScopeValue.SystemTweakScopeGameSession
                && definition.RestartRequirement ==
                    RestartRequirementValue.RestartRequirementNone
                && !definition.RequiresTarget)
            {
                SafeTweakComboBox.Items.Add(new SafeTweakOption(definition));
            }

            if (definition.Availability ==
                    SystemTweakAvailabilityValue.SystemTweakAvailabilitySupported
                && definition.Risk ==
                    SystemTweakRiskValue.SystemTweakRiskDangerous
                && definition.Scope ==
                    SystemTweakScopeValue.SystemTweakScopeGlobal
                && !definition.RequiresTarget)
            {
                DangerousTweakComboBox.Items.Add(
                    new SafeTweakOption(definition));
            }
        }

        SafeTweakComboBox.SelectedItem = SafeTweakComboBox.Items
            .OfType<SafeTweakOption>()
            .FirstOrDefault(option => StringComparer.Ordinal.Equals(
                option.Definition.Id,
                selectedTweakId))
            ?? SafeTweakComboBox.Items.OfType<SafeTweakOption>()
                .FirstOrDefault();
        DangerousTweakComboBox.SelectedItem = DangerousTweakComboBox.Items
            .OfType<SafeTweakOption>()
            .FirstOrDefault(option => StringComparer.Ordinal.Equals(
                option.Definition.Id,
                selectedDangerousTweakId))
            ?? DangerousTweakComboBox.Items.OfType<SafeTweakOption>()
                .FirstOrDefault();

        EnsureNonEmptyCatalogList(
            LaboratoryCatalogList,
            "Brak pozycji laboratoryjnych dla tej konfiguracji.");
        EnsureNonEmptyCatalogList(
            DangerCatalogList,
            "Brak pozycji sklasyfikowanych jako Dangerous lub Blocked.");
    }

    private static Border CreateCatalogCard(
        SystemTweakDefinitionMessage definition)
    {
        TextBlock title = new()
        {
            Text = definition.DisplayName,
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        TextBlock details = new()
        {
            Text = definition.Description,
            Foreground = ResourceBrush("OptimizerMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        TextBlock state = new()
        {
            Text = $"{FormatRisk(definition.Risk)} • "
                + $"{FormatAvailability(definition.Availability)} • "
                + $"restart: {FormatRestart(definition.RestartRequirement)}",
            Foreground = definition.Risk is
                    SystemTweakRiskValue.SystemTweakRiskDangerous
                    or SystemTweakRiskValue.SystemTweakRiskBlocked
                ? ResourceBrush("OptimizerDangerBrush")
                : definition.Availability ==
                    SystemTweakAvailabilityValue.SystemTweakAvailabilitySupported
                    ? ResourceBrush("OptimizerSuccessBrush")
                    : ResourceBrush("OptimizerWarningBrush"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(title);
        content.Children.Add(state);
        content.Children.Add(details);
        if (!string.IsNullOrWhiteSpace(definition.BlockingReason))
        {
            content.Children.Add(new TextBlock
            {
                Text = definition.BlockingReason,
                Foreground = ResourceBrush("OptimizerWarningBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = $"Źródło: {definition.TechnicalSource}  •  "
                + $"Restore: {definition.RestoreDescription}",
            Foreground = ResourceBrush("OptimizerMutedBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        return CreateCard(content);
    }

    private void RenderBenchmarkDecision(BenchmarkDecisionReply decision)
    {
        BenchmarkVerdict verdict = Enum.IsDefined(
            typeof(BenchmarkVerdict),
            decision.Verdict)
                ? (BenchmarkVerdict)decision.Verdict
                : BenchmarkVerdict.Invalid;
        BenchmarkVerdictText.Text = verdict switch
        {
            BenchmarkVerdict.CandidateWins =>
                "Kandydat wygrał bramę A/B",
            BenchmarkVerdict.KeepBaseline =>
                "Baseline pozostaje bezpiecznym wyborem",
            BenchmarkVerdict.Invalid =>
                "Pomiar nie spełnił wymagań jakości",
            _ when decision.RequiresAdditionalPair =>
                "Wynik niejednoznaczny — potrzebna druga para",
            _ => "Wariant zapisany — wykonaj drugą część pary",
        };
        BenchmarkVerdictText.Foreground = ResourceBrush(verdict switch
        {
            BenchmarkVerdict.CandidateWins => "OptimizerSuccessBrush",
            BenchmarkVerdict.Invalid => "OptimizerDangerBrush",
            _ => "OptimizerWarningBrush",
        });
        BenchmarkPrimaryMetricText.Text = FormatSignedPercent(
            decision.PrimaryMetricImprovementPercent);
        BenchmarkAverageFpsText.Text = FormatSignedPercent(
            decision.AverageFpsChangePercent);
        BenchmarkHitchRateText.Text =
            $"{decision.BaselineHitchRatePercent:N2}% → "
            + $"{decision.CandidateHitchRatePercent:N2}%";
        BenchmarkPairsText.Text = decision.CompletedPairs.ToString(
            CultureInfo.CurrentCulture);
    }

    private void ResetBenchmarkDecisionSurface()
    {
        BenchmarkVerdictText.Text =
            "Wynik pojawi się po zarejestrowaniu pełnej pary A/B.";
        BenchmarkVerdictText.Foreground =
            ResourceBrush("OptimizerTextBrush");
        BenchmarkPrimaryMetricText.Text = "—";
        BenchmarkAverageFpsText.Text = "—";
        BenchmarkHitchRateText.Text = "—";
        BenchmarkPairsText.Text = "0";
    }

    private static string FormatSignedPercent(double value) =>
        double.IsFinite(value)
            ? value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture)
                + "%"
            : "—";

    private void RenderProfiles(SystemOptimizationProfilesReply reply)
    {
        SavedGameProfilesList.Children.Clear();
        int invalidPayloadCount = 0;
        List<PerGameOptimizationProfile> profiles = [];
        foreach (string payload in reply.PerGameProfilesJson)
        {
            try
            {
                PerGameOptimizationProfile? profile =
                    JsonSerializer.Deserialize<PerGameOptimizationProfile>(
                        payload,
                        ProfileJsonOptions);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
            catch (JsonException)
            {
                invalidPayloadCount++;
            }
        }

        foreach (PerGameOptimizationProfile profile in profiles
            .OrderBy(item => item.GameProfileId, StringComparer.CurrentCultureIgnoreCase))
        {
            bool currentHardware = FingerprintMatches(
                profile.HardwareFingerprintHash);
            StackPanel content = new() { Spacing = 7 };
            Grid header = new() { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = profile.GameProfileId,
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            TextBlock state = new()
            {
                Text = profile.Enabled ? "Włączony" : "Wyłączony",
                Foreground = ResourceBrush(
                    profile.Enabled
                        ? "OptimizerSuccessBrush"
                        : "OptimizerMutedBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            Grid.SetColumn(state, 1);
            header.Children.Add(state);
            content.Children.Add(header);
            content.Children.Add(new TextBlock
            {
                Text = currentHardware
                    ? "Sprzęt i sterowniki zgodne z zapisanym pomiarem."
                    : "Fingerprint się zmienił — przed użyciem wykonaj ponowny test A/B.",
                Foreground = ResourceBrush(
                    currentHardware
                        ? "OptimizerMutedBrush"
                        : "OptimizerWarningBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = FormatSelections(profile.Selections),
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = $"Profil {ShortIdentifier(profile.ProfileId.ToString("D"), "—")}"
                    + $"  •  aktualizacja {profile.UpdatedAtUtc.ToLocalTime():g}",
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            SavedGameProfilesList.Children.Add(CreateCard(content));
        }

        if (profiles.Count == 0)
        {
            StackPanel empty = new() { Spacing = 5 };
            empty.Children.Add(new TextBlock
            {
                Text = "Brak zapisanych profili gier",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "Profil można zachować po jednoznacznym wyniku testu A/B.",
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            SavedGameProfilesList.Children.Add(CreateCard(empty));
        }

        if (invalidPayloadCount > 0)
        {
            SavedGameProfilesList.Children.Add(CreateCard(new TextBlock
            {
                Text = $"Pominięto uszkodzone profile: {invalidPayloadCount}.",
                Foreground = ResourceBrush("OptimizerWarningBrush"),
                TextWrapping = TextWrapping.Wrap,
            }));
        }

        RenderGlobalProfile(reply.GlobalProfileJson);
    }

    private void RenderGlobalProfile(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            GlobalProfileDetailsText.Text =
                "Nie zapisano aktywnego profilu globalnego dla tego użytkownika.";
            GlobalProfileDetailsText.Foreground =
                ResourceBrush("OptimizerMutedBrush");
            return;
        }

        try
        {
            GlobalOptimizationProfile? profile =
                JsonSerializer.Deserialize<GlobalOptimizationProfile>(
                    payload,
                    ProfileJsonOptions);
            if (profile is null)
            {
                throw new JsonException("Pusty profil globalny.");
            }

            bool currentHardware = FingerprintMatches(
                profile.HardwareFingerprintHash);
            GlobalProfileDetailsText.Text =
                (currentHardware
                    ? "Fingerprint zgodny."
                    : "Fingerprint nieaktualny — wymagany ponowny pomiar.")
                + Environment.NewLine
                + FormatSelections(profile.Selections);
            GlobalProfileDetailsText.Foreground = ResourceBrush(
                currentHardware
                    ? "OptimizerMutedBrush"
                    : "OptimizerWarningBrush");
        }
        catch (JsonException)
        {
            GlobalProfileDetailsText.Text =
                "Zapisany profil globalny jest uszkodzony i nie zostanie użyty.";
            GlobalProfileDetailsText.Foreground =
                ResourceBrush("OptimizerDangerBrush");
        }
    }

    private bool FingerprintMatches(string expected) =>
        !string.IsNullOrWhiteSpace(_hardwareFingerprintHash)
        && StringComparer.OrdinalIgnoreCase.Equals(
            expected,
            _hardwareFingerprintHash);

    private static string FormatSelections(
        IReadOnlyList<TweakSelection>? selections) =>
        selections is not { Count: > 0 }
            ? "Zmiany: brak"
            : "Zmiany: " + string.Join(
                ", ",
                selections.Select(selection =>
                    $"{selection.TweakId} = {selection.Value}"));

    private void RenderHistory(SystemOptimizationHistoryReply history)
    {
        HistoryList.Children.Clear();
        foreach (SystemOptimizationHistoryEntry entry in history.Entries)
        {
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                entry.CreatedUnixMilliseconds).ToLocalTime();
            StackPanel content = new() { Spacing = 6 };
            Grid header = new() { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            TextBlock title = new()
            {
                Text = entry.OperationKind,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            TextBlock time = new()
            {
                Text = timestamp.ToString("g", CultureInfo.CurrentCulture),
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                FontSize = 12,
            };
            Grid.SetColumn(time, 1);
            header.Children.Add(title);
            header.Children.Add(time);
            content.Children.Add(header);
            content.Children.Add(new TextBlock
            {
                Text = entry.Details,
                Foreground = entry.Success
                    ? ResourceBrush("OptimizerMutedBrush")
                    : ResourceBrush("OptimizerDangerBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            HistoryList.Children.Add(CreateCard(content));
        }

        if (HistoryList.Children.Count == 0)
        {
            StackPanel empty = new() { Spacing = 5 };
            empty.Children.Add(new TextBlock
            {
                Text = "Brak zapisanych operacji",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            empty.Children.Add(new TextBlock
            {
                Text = "Historia pojawi się po zgodzie, eksperymencie lub restore.",
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            HistoryList.Children.Add(CreateCard(empty));
        }
    }

    private void OptimizerNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag?.ToString() is string page)
        {
            ShowPage(page);
        }
    }

    private void ShowPage(string page)
    {
        OverviewPage.Visibility = page == "overview"
            ? Visibility.Visible : Visibility.Collapsed;
        HardwarePage.Visibility = page == "hardware"
            ? Visibility.Visible : Visibility.Collapsed;
        GamesPage.Visibility = page == "games"
            ? Visibility.Visible : Visibility.Collapsed;
        ExperimentsPage.Visibility = page == "experiments"
            ? Visibility.Visible : Visibility.Collapsed;
        GlobalPage.Visibility = page == "global"
            ? Visibility.Visible : Visibility.Collapsed;
        LaboratoryPage.Visibility = page == "laboratory"
            ? Visibility.Visible : Visibility.Collapsed;
        DangerPage.Visibility = page == "danger"
            ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history"
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOperationSurface()
    {
        bool busy = _operationInProgress || _refreshInProgress;
        OperationProgressRing.IsActive = busy;
        OperationProgressRing.Opacity = busy ? 1 : 0;
        RefreshButton.IsEnabled = !busy;
        if (_status is not null)
        {
            ConsentButton.IsEnabled = !busy && !_status.IsReadOnly;
            RestoreAllButton.IsEnabled = !busy && !_status.IsReadOnly;
        }

        bool canMutate = _status is
        {
            IsReadOnly: false,
            HasUserConsent: true,
        };
        SystemTweakDefinitionMessage? activeDefinition = null;
        if (_activeExperiment is not null)
        {
            _ = _catalogDefinitions.TryGetValue(
                _activeExperiment.Selection.TweakId,
                out activeDefinition);
        }

        DetectActiveGameButton.IsEnabled = !busy;
        SafeTweakComboBox.IsEnabled = !busy && _activeExperiment is null;
        DangerousTweakComboBox.IsEnabled =
            !busy && _activeExperiment is null;
        DangerousConfirmationTextBox.IsEnabled =
            !busy && _activeExperiment is null;
        PrepareExperimentButton.IsEnabled =
            !busy
            && canMutate
            && _activeGameLease is not null
            && SafeTweakComboBox.SelectedItem is SafeTweakOption
            && _activeExperiment is null;
        PrepareDangerousExperimentButton.IsEnabled =
            !busy
            && canMutate
            && _activeGameLease is not null
            && DangerousTweakComboBox.SelectedItem is SafeTweakOption
            && StringComparer.Ordinal.Equals(
                DangerousConfirmationTextBox.Text,
                SystemTweakSelectionValidator.DangerousConfirmationText)
            && _activeExperiment is null;
        RunBenchmarkButton.IsEnabled =
            !busy
            && canMutate
            && _activeExperiment?.State
                == ExperimentStateValue.ExperimentStateWaitingForScene;
        KeepResultButton.IsEnabled =
            !busy
            && canMutate
            && _activeExperiment?.State
                == ExperimentStateValue.ExperimentStateCompleted
            && activeDefinition is
            {
                Risk: SystemTweakRiskValue.SystemTweakRiskSafe,
                Scope: SystemTweakScopeValue.SystemTweakScopeGameSession,
            };
        KeepGlobalResultButton.IsEnabled =
            !busy
            && canMutate
            && _activeExperiment?.State
                == ExperimentStateValue.ExperimentStateCompleted
            && activeDefinition is
            {
                Scope: SystemTweakScopeValue.SystemTweakScopeGlobal,
            };
        CancelExperimentButton.IsEnabled =
            canMutate
            && (_benchmarkCancellation is not null
                || (!busy && _activeExperiment is not null));
    }

    private void SetDisconnectedState(string details)
    {
        TitleBarModeText.Text = "Offline";
        SetServiceState(
            "Usługa System Optimizer jest niedostępna",
            details,
            "OptimizerDangerBrush");
        OverviewStateText.Text = "Brak połączenia z usługą";
        OverviewDetailText.Text =
            "Sprawdź instalację usługi DismodeSystemAgent i jej stan.";
        ConsentButton.IsEnabled = false;
        RestoreAllButton.IsEnabled = false;
    }

    private void SetServiceState(
        string title,
        string message,
        string brushKey)
    {
        ServiceStatusTitle.Text = title;
        ServiceStatusMessage.Text = message;
        ToolTipService.SetToolTip(ServiceStatusMessage, message);
        ServiceStatusDot.Fill = ResourceBrush(brushKey);
    }

    private void ConfigureWindowIcon()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Dismode.ico");
        if (File.Exists(path))
        {
            AppWindow.SetIcon(path);
        }
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        AppWindowTitleBar titleBar = AppWindow.TitleBar;
        AccessibilitySettings accessibility = new();
        if (accessibility.HighContrast)
        {
            titleBar.BackgroundColor = null;
            titleBar.ForegroundColor = null;
            titleBar.InactiveBackgroundColor = null;
            titleBar.InactiveForegroundColor = null;
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        WindowsColor background = WindowsColor.FromArgb(255, 9, 13, 18);
        WindowsColor foreground = WindowsColor.FromArgb(255, 238, 246, 248);
        WindowsColor inactive = WindowsColor.FromArgb(255, 137, 153, 163);
        WindowsColor transparent = WindowsColor.FromArgb(0, 0, 0, 0);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor =
            WindowsColor.FromArgb(255, 31, 43, 53);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor =
            WindowsColor.FromArgb(255, 43, 58, 70);
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonInactiveForegroundColor = inactive;
    }

    private void ConfigureInitialWindowSize()
    {
        double scale = GetWindowScale();
        DisplayArea display = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        RectInt32 workArea = display.WorkArea;
        int width = Math.Min(
            workArea.Width,
            checked((int)Math.Round(1180 * scale)));
        int height = Math.Min(
            workArea.Height,
            checked((int)Math.Round(800 * scale)));
        AppWindow.Resize(new(width, height));
    }

    private void UpdateMinimumWindowSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        double scale = GetWindowScale();
        DisplayArea display = DisplayArea.GetFromWindowId(
            AppWindow.Id,
            DisplayAreaFallback.Primary);
        presenter.PreferredMinimumWidth = Math.Min(
            display.WorkArea.Width,
            checked((int)Math.Round(940 * scale)));
        presenter.PreferredMinimumHeight = Math.Min(
            display.WorkArea.Height,
            checked((int)Math.Round(640 * scale)));
    }

    private void TitleBarDragRegion_SizeChanged(
        object sender,
        SizeChangedEventArgs e) => UpdateTitleBarInsets();

    private void UpdateTitleBarInsets()
    {
        double scale = GetWindowScale();
        TitleBarLeftInset.Width = new(AppWindow.TitleBar.LeftInset / scale);
        TitleBarRightInset.Width = new(AppWindow.TitleBar.RightInset / scale);
    }

    private double GetWindowScale() =>
        RootLayout.XamlRoot?.RasterizationScale ?? 1d;

    private static bool IsExpectedClientFailure(Exception exception) =>
        exception is RpcException
            or IOException
            or HttpRequestException
            or TimeoutException
            or OperationCanceledException;

    private static bool IsExpectedOperationFailure(Exception exception) =>
        IsExpectedClientFailure(exception)
        || exception is InvalidOperationException
            or ArgumentException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception;

    private static BenchmarkVariantValue GetExpectedVariant(
        SystemExperimentReply experiment)
    {
        int captureIndex =
            experiment.CompletedBaselineCaptures
            + experiment.CompletedCandidateCaptures;
        return captureIndex % 2 == 0
            ? experiment.FirstVariant
            : experiment.FirstVariant
                == BenchmarkVariantValue.BenchmarkVariantBaseline
                    ? BenchmarkVariantValue.BenchmarkVariantCandidate
                    : BenchmarkVariantValue.BenchmarkVariantBaseline;
    }

    private static string FormatVariant(BenchmarkVariantValue variant) =>
        variant == BenchmarkVariantValue.BenchmarkVariantCandidate
            ? "kandydat"
            : "baseline";

    private static void ThrowIfFrameCaptureUnavailable(
        FrameRateSample sample)
    {
        if (sample.Status is FrameRateStatus.MissingComponent
            or FrameRateStatus.InvalidComponent
            or FrameRateStatus.AccessDenied
            or FrameRateStatus.Failed)
        {
            throw new InvalidOperationException(sample.Message);
        }
    }

    private static string ShortIdentifier(string value, string emptyValue) =>
        string.IsNullOrWhiteSpace(value)
            ? emptyValue
            : value.Length <= 12
                ? value
                : value[..8] + "…";

    private static string FormatBytes(long bytes) =>
        bytes <= 0
            ? "—"
            : $"{bytes / 1024d / 1024d / 1024d:N1} GB";

    private static long? AverageNonNegativeBytes(long? first, long? second) =>
        (first, second) switch
        {
            ( >= 0, >= 0) =>
                (first.Value / 2)
                + (second.Value / 2)
                + (((first.Value % 2) + (second.Value % 2)) / 2),
            ( >= 0, null) => first,
            (null, >= 0) => second,
            _ => null,
        };

    private static string FormatRisk(SystemTweakRiskValue risk) => risk switch
    {
        SystemTweakRiskValue.SystemTweakRiskSafe => "Safe",
        SystemTweakRiskValue.SystemTweakRiskExperimental => "Experimental",
        SystemTweakRiskValue.SystemTweakRiskDangerous => "Dangerous",
        SystemTweakRiskValue.SystemTweakRiskBlocked => "Blocked",
        _ => "Nieznane ryzyko",
    };

    private static string FormatAvailability(
        SystemTweakAvailabilityValue availability) => availability switch
        {
            SystemTweakAvailabilityValue.SystemTweakAvailabilitySupported =>
                "Obsługiwane",
            SystemTweakAvailabilityValue.SystemTweakAvailabilityUnsupported =>
                "Unsupported",
            SystemTweakAvailabilityValue.SystemTweakAvailabilityBlockedByPolicy =>
                "Zablokowane polityką",
            _ => "Nieznane",
        };

    private static string FormatRestart(
        RestartRequirementValue requirement) => requirement switch
        {
            RestartRequirementValue.RestartRequirementNone => "brak",
            RestartRequirementValue.RestartRequirementGame => "gra",
            RestartRequirementValue.RestartRequirementSignOut => "wylogowanie",
            RestartRequirementValue.RestartRequirementWindows => "Windows",
            _ => "nieznany",
        };

    private static void EnsureNonEmptyCatalogList(
        StackPanel list,
        string message)
    {
        if (list.Children.Count == 0)
        {
            list.Children.Add(CreateCard(new TextBlock
            {
                Text = message,
                Foreground = ResourceBrush("OptimizerMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            }));
        }
    }

    private static Border CreateCard(UIElement content) => new()
    {
        Padding = new(20),
        CornerRadius = new(14),
        Background = ResourceBrush("OptimizerCardBrush"),
        BorderBrush = ResourceBrush("OptimizerBorderBrush"),
        BorderThickness = new(1),
        Child = content,
    };

    private static Brush ResourceBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    private sealed record SafeTweakOption(
        SystemTweakDefinitionMessage Definition)
    {
        public override string ToString() => Definition.DisplayName;
    }
}

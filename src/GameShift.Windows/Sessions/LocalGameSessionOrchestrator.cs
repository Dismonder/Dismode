using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.History;
using GameShift.Core.Journal;
using GameShift.Core.Profiles;
using GameShift.Core.Recovery;
using GameShift.Core.Sessions;
using GameShift.Core.Transactions;
using GameShift.Windows.Cpu;
using GameShift.Windows.NativeInterop;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;

namespace GameShift.Windows.Sessions;

public sealed class LocalGameSessionOrchestrator : IAsyncDisposable
{
    private static readonly TimeSpan PlanLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShutdownReservationLifetime =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultMonitorInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ForcedGameExitTimeout =
        TimeSpan.FromSeconds(3);
    private const int RequiredMissingProcessObservations = 3;
    private const int MaximumBackgroundApplications = 64;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.General);

    private readonly IGameProfileRepository _profiles;
    private readonly ISessionHistoryRepository _history;
    private readonly IRecoveryJournal _journal;
    private readonly SessionCheckpointWriter _checkpointWriter;
    private readonly ManualGameProfileLauncher _launcher;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly IProcessParentMapProvider _processParentMapProvider;
    private readonly IFrameRateProvider _frameRateProvider;
    private readonly BackgroundApplicationGuard _backgroundApplicationGuard;
    private readonly IGameOptimizationPreferencesRepository?
        _optimizationPreferences;
    private readonly SavedBackgroundRuleResolver _savedRuleResolver;
    private readonly IGameMetadataRepository? _gameMetadataRepository;
    private readonly GameMetadataRefreshService? _metadataRefreshService;
    private readonly ISystemGameProfileCoordinator _systemProfileCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _monitorInterval;
    private readonly bool _proBalanceEnabled;
    private readonly ProBalanceSettings? _proBalanceSettings;
    private readonly Func<ICpuProcessSource> _cpuProcessSourceFactory;
    private readonly Func<SessionId, IProBalanceActuator>?
        _proBalanceActuatorFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Migawka metadanych i jej zapis ida pod jedna bramka: monitor i ksiega
    // ograniczen pisza punkty kontrolne rownolegle, a odtwarzanie czyta
    // OSTATNI. Bez tego starsza migawka zapisana pozniej kasowalaby swiezszy
    // stan — na przyklad ograniczenie zameldowane chwile wczesniej. Osobna od
    // _gate, bo ksiega nie moze go brac.
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private PendingPlan? _pendingPlan;
    private ActiveRuntime? _activeSession;
    private DateTimeOffset? _shutdownReservedUntilUtc;
    private Task _monitorTask = Task.CompletedTask;
    private bool _initialized;
    private bool _disposed;

    public LocalGameSessionOrchestrator(
        IGameProfileRepository profiles,
        ISessionHistoryRepository history,
        IRecoveryJournal journal,
        ManualGameProfileLauncher? launcher = null,
        IProcessIdentityProvider? identityProvider = null,
        BackgroundApplicationGuard? backgroundApplicationGuard = null,
        TimeProvider? timeProvider = null,
        TimeSpan? monitorInterval = null,
        IProcessParentMapProvider? processParentMapProvider = null,
        IFrameRateProvider? frameRateProvider = null,
        IGameOptimizationPreferencesRepository? optimizationPreferences = null,
        SavedBackgroundRuleResolver? savedRuleResolver = null,
        IGameMetadataRepository? gameMetadataRepository = null,
        GameMetadataRefreshService? metadataRefreshService = null,
        ISystemGameProfileCoordinator? systemProfileCoordinator = null,
        bool enableProBalance = false,
        ProBalanceSettings? proBalanceSettings = null,
        Func<ICpuProcessSource>? cpuProcessSourceFactory = null,
        Func<SessionId, IProBalanceActuator>? proBalanceActuatorFactory = null)
    {
        _profiles = profiles;
        _history = history;
        _journal = journal;
        _checkpointWriter = new(journal);
        _launcher = launcher ?? new ManualGameProfileLauncher();
        _identityProvider =
            identityProvider ?? new ProcessIdentityProvider();
        _processParentMapProvider =
            processParentMapProvider ?? new ProcessParentMapProvider();
        _frameRateProvider =
            frameRateProvider ?? new PresentMonFrameRateProvider();
        _backgroundApplicationGuard =
            backgroundApplicationGuard
            ?? new BackgroundApplicationGuard(_identityProvider);
        _optimizationPreferences = optimizationPreferences;
        _savedRuleResolver =
            savedRuleResolver ?? new SavedBackgroundRuleResolver();
        _gameMetadataRepository =
            gameMetadataRepository ?? profiles as IGameMetadataRepository;
        _metadataRefreshService = _gameMetadataRepository is null
            ? null
            : metadataRefreshService ?? new GameMetadataRefreshService();
        _systemProfileCoordinator =
            systemProfileCoordinator ?? NullSystemGameProfileCoordinator.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _monitorInterval = monitorInterval ?? DefaultMonitorInterval;

        // Domyslnie wylaczone. Reszta tego, co GameShift rusza, przechodzi
        // przez plan zatwierdzony przez uzytkownika; ta petla siega po procesy,
        // ktorych nikt nie wskazal, wiec wlacza sie dopiero razem z wlasnym
        // przelacznikiem w interfejsie.
        _proBalanceEnabled = enableProBalance;

        // Progi sa wystawione, bo maja byc dostrajane pomiarem czasow klatek,
        // a nie przyjete raz na zawsze.
        _proBalanceSettings = proBalanceSettings;

        // Szew do testow. Domyslnie te same obiekty co zawsze — chodzi o to,
        // zeby dalo sie sprawdzic, czy przelacznik w interfejsie faktycznie
        // konczy sie ograniczeniem procesu, bez czekania na prawdziwe
        // obciazenie procesora.
        _cpuProcessSourceFactory =
            cpuProcessSourceFactory ?? (static () => new CpuProcessSampler());
        _proBalanceActuatorFactory = proBalanceActuatorFactory;

        if (_monitorInterval <= TimeSpan.Zero
            || _monitorInterval > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(monitorInterval),
                _monitorInterval,
                "The session monitor interval must be positive and at most one minute.");
        }
    }

    public async ValueTask InitializeAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _profiles.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
            await _history.InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
            await RecoverUserSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionPlanPreview> PrepareAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken)
    {
        return await PrepareAsync(
                profileId,
                backgroundApplications: [],
                desiredGamePriority: null,
                cancellationToken,
                useSavedBackgroundRules: false)
            .ConfigureAwait(false);
    }

    public async ValueTask<SessionPlanPreview> PrepareAsync(
        GameProfileId profileId,
        IReadOnlyList<BackgroundApplicationSelection>
            backgroundApplications,
        CancellationToken cancellationToken)
    {
        return await PrepareAsync(
                profileId,
                backgroundApplications,
                desiredGamePriority: null,
                cancellationToken,
                useSavedBackgroundRules: false)
            .ConfigureAwait(false);
    }

    public async ValueTask<SessionPlanPreview> PrepareAsync(
        GameProfileId profileId,
        IReadOnlyList<BackgroundApplicationSelection>
            backgroundApplications,
        ProcessPriorityClass? desiredGamePriority,
        CancellationToken cancellationToken,
        bool useSavedBackgroundRules = false)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(backgroundApplications);
        if (backgroundApplications.Count > MaximumBackgroundApplications)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backgroundApplications),
                $"Można wybrać maksymalnie {MaximumBackgroundApplications} aplikacji.");
        }

        if (useSavedBackgroundRules
            && backgroundApplications.Count > 0)
        {
            throw new ArgumentException(
                "Zapisanych reguł nie można łączyć z ręczną listą procesów.",
                nameof(backgroundApplications));
        }

        if (useSavedBackgroundRules
            && desiredGamePriority is not null)
        {
            throw new ArgumentException(
                "Tryb zapisanych reguł zachowuje normalny priorytet gry.",
                nameof(desiredGamePriority));
        }

        if (desiredGamePriority is not null
            and not (
                ProcessPriorityClass.AboveNormal
                or ProcessPriorityClass.High))
        {
            throw new ArgumentOutOfRangeException(
                nameof(desiredGamePriority),
                desiredGamePriority,
                "Priorytet gry może pozostać normalny albo zostać ustawiony "
                + "na AboveNormal lub High.");
        }

        if (backgroundApplications
            .GroupBy(selection => selection.ProcessId)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "Ta sama aplikacja została wybrana więcej niż raz.",
                nameof(backgroundApplications));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShutdownNotReserved();
            if (_activeSession is not null)
            {
                throw new InvalidOperationException(
                    "An optimization session is already active.");
            }

            ManualGameProfile profile =
                await _profiles.FindAsync(profileId, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    "The selected game profile does not exist.");
            if (!profile.IsEnabled)
            {
                throw new InvalidOperationException(
                    "The selected game profile is disabled.");
            }

            string currentHash =
                await ExecutableFileHasher.ComputeSha256Async(
                        profile.ExecutablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    currentHash,
                    profile.ExecutableSha256))
            {
                throw new InvalidOperationException(
                    "The game executable changed after profile approval.");
            }

            IReadOnlyList<BackgroundApplicationSelection>
                selectionsToApprove = backgroundApplications;
            SavedRulePlanSummary? savedRuleSummary = null;
            if (useSavedBackgroundRules)
            {
                IGameOptimizationPreferencesRepository repository =
                    _optimizationPreferences
                    ?? throw new InvalidOperationException(
                        "Repozytorium zapisanych reguł gry jest niedostępne.");
                GameOptimizationPreferences preferences =
                    await repository.LoadOptimizationPreferencesAsync(
                            profileId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (preferences.ProfileId != profileId)
                {
                    throw new InvalidDataException(
                        "Wczytane preferencje należą do innego profilu gry.");
                }

                // One-click launch applies only previously approved
                // background-process rules. A stored priority must never
                // silently promote a game; AboveNormal and High remain
                // explicit, per-plan experimental choices.
                desiredGamePriority = null;
                SavedBackgroundRuleResolution resolution =
                    _savedRuleResolver.Resolve(
                        preferences,
                        profile.ExecutablePath,
                        Process.GetCurrentProcess().SessionId,
                        MaximumBackgroundApplications);
                selectionsToApprove = resolution.Selections;
                savedRuleSummary = new(
                    resolution.ConfiguredRuleCount,
                    resolution.MatchedRuleCount,
                    resolution.Selections.Count,
                    ApprovedProcessCount: 0);
            }

            List<PlannedBackgroundApplication> approvedApplications = [];
            foreach (BackgroundApplicationSelection selection
                         in selectionsToApprove)
            {
                try
                {
                    approvedApplications.Add(
                        await _backgroundApplicationGuard
                            .ApproveAsync(
                                selection,
                                profile.ExecutablePath,
                                cancellationToken)
                            .ConfigureAwait(false));
                }
                catch (InvalidOperationException)
                    when (useSavedBackgroundRules)
                {
                    // A saved rule can become inapplicable between discovery
                    // and approval. One-click launch skips it fail-closed.
                }
            }

            if (savedRuleSummary is not null)
            {
                savedRuleSummary = savedRuleSummary with
                {
                    ApprovedProcessCount = approvedApplications.Count,
                };
            }

            OptimizationSessionState state = OptimizationSessionState.Idle;
            state = SessionStateMachine.Transition(
                state,
                OptimizationSessionState.Discovering);
            state = SessionStateMachine.Transition(
                state,
                OptimizationSessionState.Profiling);
            state = SessionStateMachine.Transition(
                state,
                OptimizationSessionState.Planning);
            state = SessionStateMachine.Transition(
                state,
                OptimizationSessionState.AwaitingApproval);

            DateTimeOffset now = _timeProvider.GetUtcNow();
            _pendingPlan = new(
                Guid.NewGuid(),
                SessionId.Create(),
                profile,
                currentHash,
                approvedApplications,
                desiredGamePriority,
                savedRuleSummary,
                state,
                now + PlanLifetime);
            return ToPreview(_pendingPlan);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionId?> GetExpectedStartSessionIdAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pendingPlan is not null
                && _pendingPlan.PlanId == planId
                && _pendingPlan.ExpiresAtUtc >= _timeProvider.GetUtcNow())
            {
                return _pendingPlan.SessionId;
            }

            return _activeSession is not null
                && _activeSession.PlanId == planId
                    ? _activeSession.SessionId
                    : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GameSessionSnapshot> StartAsync(
        Guid planId,
        SessionId sessionId,
        CancellationToken cancellationToken,
        bool enableFrameRateTracking = true,
        bool? enableProBalance = null)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShutdownNotReserved();
            if (_activeSession is not null)
            {
                if (_activeSession.PlanId == planId
                    && _activeSession.SessionId == sessionId)
                {
                    return ToSnapshot(
                        _activeSession,
                        "The approved game session is already active.");
                }

                throw new InvalidOperationException(
                    "A different optimization session is already active.");
            }

            PendingPlan plan = _pendingPlan is not null
                && _pendingPlan.PlanId == planId
                && _pendingPlan.SessionId == sessionId
                    ? _pendingPlan
                    : throw new InvalidOperationException(
                        "The approved session plan was not found.");
            if (plan.ExpiresAtUtc < _timeProvider.GetUtcNow())
            {
                _pendingPlan = null;
                throw new InvalidOperationException(
                    "The approved session plan expired.");
            }

            ManualGameProfile currentProfile =
                await _profiles
                    .FindAsync(plan.Profile.ProfileId, cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The selected game profile was removed.");
            EnsureProfileStillMatches(plan, currentProfile);
            string currentHash =
                await ExecutableFileHasher.ComputeSha256Async(
                        currentProfile.ExecutablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    currentHash,
                    plan.ExecutableHash))
            {
                _pendingPlan = null;
                throw new InvalidOperationException(
                    "The game executable changed after plan approval.");
            }

            plan.State = SessionStateMachine.Transition(
                plan.State,
                OptimizationSessionState.Preflight);
            plan.State = SessionStateMachine.Transition(
                plan.State,
                OptimizationSessionState.Snapshotting);
            DateTimeOffset startedAtUtc = _timeProvider.GetUtcNow();

            // Cwiartka maszyny dla procesow tla — liczona raz na sesje i
            // zapisywana w metadanych, zeby odtwarzanie cofalo te maske,
            // ktora nalozono. Zero oznacza, ze maszyna sie nie kwalifikuje
            // (za malo watkow albo wiele grup procesorow) i maski nie
            // nakladamy w ogole.
            ulong backgroundMask = ResolveBackgroundAffinityMask();
            SessionRecoveryMetadata metadata = new(
                currentProfile.ProfileId,
                currentProfile.DisplayName,
                startedAtUtc,
                RootProcess: null,
                TrackedGameProcesses: null,
                plan.BackgroundApplications
                    .Select(ToRecoveryMetadata)
                    .ToArray(),
                GamePriority: null,
                AppliedActionCount: 0,
                FrameRateTrackingEnabled: enableFrameRateTracking,
                BackgroundAffinityMask: backgroundMask);
            await RecordCheckpointAsync(
                    plan.SessionId,
                    SessionCheckpoint.SnapshotComplete,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {
                plan.State = SessionStateMachine.Transition(
                    plan.State,
                    OptimizationSessionState.LaunchingGame);

                LaunchedGameProcess launched =
                    await _launcher.LaunchOrAttachAfterVerificationAsync(
                            currentProfile,
                            currentHash,
                            _lifetime.Token)
                        .ConfigureAwait(false);
                metadata = metadata with
                {
                    RootProcess = launched.Identity,
                    TrackedGameProcesses = [launched.Identity],
                    GamePriority = plan.GamePriority is null
                        ? null
                        : ToRecoveryMetadata(
                            plan.GamePriority,
                            launched.Identity),
                };
                await RecordCheckpointAsync(
                        plan.SessionId,
                        SessionCheckpoint.GameLaunched,
                        metadata,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                plan.State = SessionStateMachine.Transition(
                    plan.State,
                    OptimizationSessionState.Applying);
                int gamePriorityActionCount = 0;
                if (plan.GamePriority is not null)
                {
                    await ApplyGamePriorityAsync(
                            plan.SessionId,
                            plan.GamePriority,
                            launched.Identity,
                            startedAtUtc,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    gamePriorityActionCount = 1;
                }

                // Przypiecie gry do rdzeni wydajnych. Na jednorodnym
                // procesorze polityka odmawia i nic sie nie dzieje.
                int affinityActionCount = await ApplyGameAffinityAsync(
                        plan.SessionId,
                        launched.Identity,
                        startedAtUtc,
                        enableProBalance ?? _proBalanceEnabled,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                BackgroundBundleOutcome bundle =
                    await ApplyBackgroundApplicationsAsync(
                            plan.SessionId,
                            plan.BackgroundApplications,
                            startedAtUtc,
                            backgroundMask,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                int appliedActionCount = bundle.AppliedCount;
                SystemGameProfileOperationResult systemProfile =
                    await _systemProfileCoordinator.ActivateAsync(
                            currentProfile.ProfileId,
                            launched.Identity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                metadata = metadata with
                {
                    AppliedActionCount =
                        appliedActionCount + gamePriorityActionCount,
                    SystemProfileActive = systemProfile.WasApplied,
                };
                await RecordCheckpointAsync(
                        plan.SessionId,
                        SessionCheckpoint.ProcessesApplied,
                        metadata,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                plan.State = SessionStateMachine.Transition(
                    plan.State,
                    OptimizationSessionState.Verifying);
                plan.State = SessionStateMachine.Transition(
                    plan.State,
                    OptimizationSessionState.Active);
                await RecordCheckpointAsync(
                        plan.SessionId,
                        SessionCheckpoint.SessionActivated,
                        metadata,
                        CancellationToken.None)
                    .ConfigureAwait(false);

                ActiveRuntime runtime = new(
                    plan.PlanId,
                    plan.SessionId,
                    currentProfile.ProfileId,
                    currentProfile.DisplayName,
                    startedAtUtc,
                    launched.Identity,
                    CreateGameProcessTreeTracker(
                        launched.Identity,
                        [launched.Identity]),
                    plan.State,
                    plan.BackgroundApplications,
                    metadata.GamePriority,
                    appliedActionCount
                        + gamePriorityActionCount
                        + affinityActionCount,
                    recoveredFromHostCrash: false,
                    frameRateTrackingEnabled: enableFrameRateTracking,
                    systemProfileActive: systemProfile.WasApplied,
                    backgroundAffinityMask: backgroundMask);
                runtime.FrameRate = enableFrameRateTracking
                    ? await _frameRateProvider.SampleAsync(
                            [launched.Identity.RuntimeKey.ProcessId],
                            CancellationToken.None)
                        .ConfigureAwait(false)
                    : FrameRateSample.Disabled();
                _activeSession = runtime;
                _pendingPlan = null;
                StartProBalance(
                    runtime,
                    enableProBalance ?? _proBalanceEnabled);
                StartMonitor(runtime);
                int closedApplicationCount =
                    plan.BackgroundApplications.Count(application =>
                        application.ActionMode
                            == BackgroundProcessActionMode
                                .CloseAndRestore);
                int loweredProcessCount =
                    plan.BackgroundApplications.Count(application =>
                        application.ActionMode
                            != BackgroundProcessActionMode
                                .CloseAndRestore);
                int ecoQosProcessCount =
                    plan.BackgroundApplications.Count(application =>
                        application.ActionMode is
                            BackgroundProcessActionMode.LowerPriorityAndEcoQos
                            or BackgroundProcessActionMode.RestrainBackground);
                // Liczba masek faktycznie nalozonych, nie zaplanowanych:
                // maska jest best-effort i proces, ktory sam zawezil sobie
                // affinity, jej nie przyjmie. Komunikat ma mowic prawde.
                int confinedProcessCount = bundle.ConfinedCount;
                return ToSnapshot(
                    runtime,
                    (launched.WasAlreadyRunning
                        ? "Dołączono do uruchomionej gry. "
                        : "Gra została uruchomiona. ")
                    + "Zamknięte: "
                    + $"{closedApplicationCount}; ograniczone: "
                    + $"{loweredProcessCount}; EcoQoS: "
                    + $"{ecoQosProcessCount}; na rdzeniach tła: "
                    + $"{confinedProcessCount}; Windows potwierdził priorytet: "
                    + $"{DescribePriority(plan.GamePriority)}."
                    + DescribeSavedRules(plan.SavedRuleSummary));
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or InvalidOperationException
                    or Win32Exception
                    or UnauthorizedAccessException
                    or OperationCanceledException
                    or TimeoutException)
            {
                _pendingPlan = null;
                BackgroundRecoveryTotals priorityRecovery =
                    await RestoreGamePriorityAsync(
                            plan.SessionId,
                            metadata.GamePriority,
                            startedAtUtc,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                BackgroundRecoveryTotals applicationRecovery =
                    await RestoreBackgroundApplicationsAsync(
                            plan.SessionId,
                            plan.BackgroundApplications,
                            startedAtUtc,
                            backgroundMask,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                BackgroundRecoveryTotals recovery = MergeRecoveryTotals(
                    priorityRecovery,
                    applicationRecovery);
                if (recovery.ErrorCount == 0)
                {
                    await RecordCheckpointAsync(
                            plan.SessionId,
                            SessionCheckpoint.ProcessesRestored,
                            metadata,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await FinalizeFailedLaunchAsync(
                            plan,
                            metadata,
                            recovery,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GameSessionSnapshot> SetFrameRateTrackingAsync(
        SessionId sessionId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveRuntime runtime = _activeSession is not null
                && _activeSession.SessionId == sessionId
                    ? _activeSession
                    : throw new InvalidOperationException(
                        "The requested game session is not active.");

            if (runtime.FrameRateTrackingEnabled == enabled)
            {
                return ToSnapshot(
                    runtime,
                    enabled
                        ? "Pomiar FPS jest już włączony."
                        : "Pomiar FPS jest już wyłączony w ustawieniach GameShift.");
            }

            runtime.FrameRateTrackingEnabled = enabled;
            await _frameRateProvider.StopAsync(cancellationToken)
                .ConfigureAwait(false);
            runtime.FrameRate = enabled
                ? FrameRateSample.WaitingForGame(
                    runtime.RootProcess.RuntimeKey.ProcessId)
                : FrameRateSample.Disabled();
            return ToSnapshot(
                runtime,
                enabled
                    ? "Pomiar FPS został włączony. PresentMon rozpocznie pracę przy następnej próbce."
                    : "Pomiar FPS został wyłączony. PresentMon został zatrzymany.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GameSessionSnapshot?> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _activeSession is null
                ? null
                : ToSnapshot(
                    _activeSession,
                    "The game session is being monitored locally.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionShutdownReadiness> GetShutdownReadinessAsync(
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool hasActiveSession = _activeSession is not null;
            if (_pendingPlan is not null &&
                _pendingPlan.ExpiresAtUtc < _timeProvider.GetUtcNow())
            {
                _pendingPlan = null;
            }

            bool hasPreparedPlan = _pendingPlan is not null;
            bool canShutdown = !hasActiveSession && !hasPreparedPlan;
            string message = hasActiveSession
                ? "An optimization session is active. Restore it before shutdown."
                : hasPreparedPlan
                    ? "An approved session plan is waiting to start."
                    : "Gaming components can be stopped safely.";
            return new(
                canShutdown,
                hasActiveSession,
                hasPreparedPlan,
                message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionShutdownReadiness> ReserveShutdownAsync(
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            ExpireShutdownReservation(now);
            bool hasActiveSession = _activeSession is not null;
            if (_pendingPlan is not null && _pendingPlan.ExpiresAtUtc < now)
            {
                _pendingPlan = null;
            }

            bool hasPreparedPlan = _pendingPlan is not null;
            if (hasActiveSession || hasPreparedPlan)
            {
                return new(
                    false,
                    hasActiveSession,
                    hasPreparedPlan,
                    hasActiveSession
                        ? "An optimization session is active. Restore it before shutdown."
                        : "An approved session plan is waiting to start.");
            }

            _shutdownReservedUntilUtc = now + ShutdownReservationLifetime;
            return new(
                true,
                false,
                false,
                "Gaming component shutdown was reserved safely.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionId?> GetActiveSessionIdAsync(
        CancellationToken cancellationToken)
    {
        GameSessionSnapshot? active =
            await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return active?.SessionId;
    }

    public async ValueTask<GameSessionSnapshot> RestoreAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveRuntime runtime = _activeSession is not null
                && _activeSession.SessionId == sessionId
                    ? _activeSession
                    : throw new InvalidOperationException(
                        "The requested game session is not active.");

            if (runtime.State == OptimizationSessionState.Active)
            {
                runtime.State = SessionStateMachine.Transition(
                    runtime.State,
                    OptimizationSessionState.UserRestoreRequested);
            }
            else if (runtime.State != OptimizationSessionState.RecoveryRequired)
            {
                throw new InvalidOperationException(
                    "Sesja nie jest gotowa do przywrócenia.");
            }

            return await CompleteRuntimeAsync(
                    runtime,
                    SessionCompletionStatus.Completed,
                    "Sesja została zakończona, a wybrane aplikacje przywrócono. "
                    + "Proces gry pozostał uruchomiony.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<GameSessionSnapshot> CloseGameAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        return await CloseGameAsync(
                sessionId,
                forceTermination: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<GameSessionSnapshot> CloseGameAsync(
        SessionId sessionId,
        bool forceTermination,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ActiveRuntime runtime = _activeSession is not null
                && _activeSession.SessionId == sessionId
                    ? _activeSession
                    : throw new InvalidOperationException(
                        "The requested game session is not active.");
            if (runtime.State != OptimizationSessionState.Active)
            {
                throw new InvalidOperationException(
                    "Grę można zamknąć tylko podczas aktywnego Trybu gry.");
            }

            await RecordRuntimeCheckpointAsync(
                    runtime,
                    SessionCheckpoint.GameCloseRequested,
                    cancellationToken)
                .ConfigureAwait(false);

            GameProcessTreeObservation observation =
                await runtime.ProcessTree.ObserveAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (observation.NewlyDiscoveredCount > 0)
            {
                ClearInheritedCornerFromGame(
                    runtime,
                    observation.RunningProcesses);
                await RecordRuntimeCheckpointAsync(
                        runtime,
                        SessionCheckpoint.GameProcessTreeObserved,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (forceTermination)
            {
                int terminatedCount =
                    await ForceTerminateVerifiedGameProcessesAsync(
                            runtime,
                            observation.RunningProcesses,
                            cancellationToken)
                        .ConfigureAwait(false);
                await RecordRuntimeCheckpointAsync(
                        runtime,
                        SessionCheckpoint.GameForceTerminated,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                runtime.State = SessionStateMachine.Transition(
                    runtime.State,
                    OptimizationSessionState.GameExited);
                return await CompleteRuntimeAsync(
                        runtime,
                        runtime.RecoveredFromHostCrash
                            ? SessionCompletionStatus.RecoveredAfterCrash
                            : SessionCompletionStatus.Completed,
                        $"Natychmiast zakończono zweryfikowane procesy gry: "
                        + $"{terminatedCount}. GameShift przywrócił akcje tła.",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (!await TryRequestGracefulGameCloseAsync(
                    runtime,
                    observation.RunningProcesses,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Gra nie udostępnia okna przyjmującego bezpieczne "
                    + "żądanie zamknięcia. GameShift nie użył wymuszonego kill.");
            }

            return ToSnapshot(
                runtime,
                "Wysłano zwykłe żądanie zamknięcia gry. Po jej wyjściu "
                + "GameShift automatycznie przywróci wszystkie akcje.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _lifetime.Cancel();

        // Nadzorca ma wlasny cykl zycia i nie sluchа tokenu sesji. Bez tego
        // jego petla przezywa orkiestrator, a to, co obnizyl, zostaje
        // obnizone — widoczne, gdy sesja konczy sie inaczej niz przez
        // Restore, na przyklad przy zamknieciu aplikacji.
        if (_activeSession is { } runtime)
        {
            await StopProBalanceAsync(runtime).ConfigureAwait(false);
        }

        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _frameRateProvider.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
        _checkpointGate.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async ValueTask RecoverUserSessionAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RecoveryJournalEntry> entries =
            await _journal.ReadAllAsync(cancellationToken)
                .ConfigureAwait(false);
        IGrouping<Guid, RecoveryJournalEntry>[] incompleteSessions = entries
            .Where(entry =>
                entry.EventKind
                    == JournalEventKind.SessionCheckpointRecorded)
            .GroupBy(entry => entry.SessionId)
            .Where(group =>
                group.OrderBy(entry => entry.Sequence)
                    .Last()
                    .SessionCheckpoint
                    != SessionCheckpoint.ReconciliationComplete)
            .OrderBy(group => group.Max(entry => entry.Sequence))
            .ToArray();

        foreach (IGrouping<Guid, RecoveryJournalEntry> group
                     in incompleteSessions)
        {
            RecoveryJournalEntry[] ordered = group
                .OrderBy(entry => entry.Sequence)
                .ToArray();
            RecoveryJournalEntry? metadataEntry = ordered
                .LastOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(entry.Details));
            if (metadataEntry?.Details is null)
            {
                continue;
            }

            SessionRecoveryMetadata metadata = DeserializeMetadata(
                metadataEntry.Details);
            PlannedBackgroundApplication[] backgroundApplications =
                (metadata.BackgroundApplications ?? [])
                    .Select(FromRecoveryMetadata)
                    .ToArray();
            PlannedBackgroundApplication[] restrainedProcesses =
                (metadata.RestrainedProcesses ?? [])
                    .Select(FromRecoveryMetadata)
                    .ToArray();
            SessionId sessionId = new(group.Key);
            bool launchWasRecorded = ordered.Any(entry =>
                entry.SessionCheckpoint is
                    SessionCheckpoint.GameLaunched
                    or SessionCheckpoint.SessionActivated);
            if (launchWasRecorded
                && metadata.RootProcess is not null
                && _activeSession is null)
            {
                GameProcessTreeSessionTracker processTree =
                    CreateGameProcessTreeTracker(
                        metadata.RootProcess,
                        metadata.TrackedGameProcesses);
                GameProcessTreeObservation observation =
                    await processTree.ObserveAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (observation.HasRunningProcess)
                {
                    // Ograniczenia reaktywne nie maja juz nadzorcy, ktory by
                    // je zdjal, gdy proces sie uspokoi — a GameShift nie jest
                    // niczyim planista na stale. Oddajemy je od razu;
                    // zaplanowane aplikacje zostaja do konca sesji, bo na nie
                    // uzytkownik sie zgodzil.
                    // Punkt kontrolny sprzed pola z maska: maske bierzemy
                    // z dziennika tamtej sesji, nigdy z dzisiejszej
                    // topologii. Brak sladu znaczy, ze nic nie przypieto.
                    ulong recoveredMask = metadata.BackgroundAffinityMask
                        ?? ReadHistoricalBackgroundMask(
                            entries,
                            group.Key,
                            restrainedProcesses.Concat(backgroundApplications))
                        ?? 0;
                    BackgroundRecoveryTotals liveRestraintRecovery =
                        restrainedProcesses.Length > 0
                            ? await RestoreBackgroundApplicationsAsync(
                                    sessionId,
                                    restrainedProcesses,
                                    metadata.StartedAtUtc,
                                    recoveredMask,
                                    cancellationToken)
                                .ConfigureAwait(false)
                            : new(0, 0, 0);
                    // To, czego nie udalo sie oddac, zostaje w wznowionej
                    // sesji i wraca do proby przy jej zamknieciu. Zapisanie
                    // pustej listy odebraloby tej probie podstawe.
                    PlannedBackgroundApplication[] unresolvedRestraints =
                        liveRestraintRecovery.ErrorCount > 0
                            ? restrainedProcesses
                            : [];

                    ActiveRuntime recovered = new(
                        planId: Guid.Empty,
                        sessionId,
                        metadata.ProfileId,
                        metadata.GameDisplayName,
                        metadata.StartedAtUtc,
                        metadata.RootProcess,
                        processTree,
                        OptimizationSessionState.Active,
                        backgroundApplications,
                        metadata.GamePriority,
                        metadata.AppliedActionCount,
                        recoveredFromHostCrash: true,
                        frameRateTrackingEnabled:
                            metadata.FrameRateTrackingEnabled,
                        systemProfileActive:
                            metadata.SystemProfileActive,
                        dynamicRestraints: unresolvedRestraints,
                        backgroundAffinityMask: recoveredMask);
                    recovered.FrameRate = metadata.FrameRateTrackingEnabled
                        ? await _frameRateProvider.SampleAsync(
                                observation.RunningProcesses
                                    .Select(identity =>
                                        identity.RuntimeKey.ProcessId)
                                    .ToArray(),
                                cancellationToken)
                            .ConfigureAwait(false)
                        : FrameRateSample.Disabled();
                    _activeSession = recovered;
                    if (observation.NewlyDiscoveredCount > 0
                        || restrainedProcesses.Length > 0)
                    {
                        await RecordRuntimeCheckpointAsync(
                                recovered,
                                SessionCheckpoint.GameProcessTreeObserved,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    StartMonitor(recovered);
                    continue;
                }
            }

            BackgroundRecoveryTotals priorityRecovery =
                await RestoreGamePriorityAsync(
                        sessionId,
                        metadata.GamePriority,
                        metadata.StartedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            ulong finalMask = metadata.BackgroundAffinityMask
                ?? ReadHistoricalBackgroundMask(
                    entries,
                    group.Key,
                    restrainedProcesses.Concat(backgroundApplications))
                ?? 0;
            BackgroundRecoveryTotals restraintRecovery =
                await RestoreBackgroundApplicationsAsync(
                        sessionId,
                        restrainedProcesses,
                        metadata.StartedAtUtc,
                        finalMask,
                        cancellationToken)
                    .ConfigureAwait(false);
            BackgroundRecoveryTotals applicationRecovery =
                await RestoreBackgroundApplicationsAsync(
                        sessionId,
                        backgroundApplications,
                        metadata.StartedAtUtc,
                        finalMask,
                        cancellationToken)
                    .ConfigureAwait(false);
            BackgroundRecoveryTotals recovery = MergeRecoveryTotals(
                MergeRecoveryTotals(priorityRecovery, restraintRecovery),
                applicationRecovery);
            if (recovery.ErrorCount > 0)
            {
                continue;
            }

            await RecordCheckpointAsync(
                    sessionId,
                    SessionCheckpoint.ProcessesRestored,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            await FinalizeRecoveredSessionAsync(
                    sessionId,
                    metadata,
                    launchWasRecorded
                        ? SessionCompletionStatus.RecoveredAfterCrash
                        : SessionCompletionStatus.FailedBeforeApply,
                    recovery,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask FinalizeRecoveredSessionAsync(
        SessionId sessionId,
        SessionRecoveryMetadata metadata,
        SessionCompletionStatus status,
        BackgroundRecoveryTotals recovery,
        CancellationToken cancellationToken)
    {
        await RecordCheckpointAsync(
                sessionId,
                SessionCheckpoint.RestoreStarted,
                metadata,
                cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset endedAtUtc = _timeProvider.GetUtcNow();
        await _history.AddAsync(
                new(
                    sessionId,
                    metadata.ProfileId,
                    metadata.GameDisplayName,
                    metadata.StartedAtUtc,
                    endedAtUtc,
                    status,
                    appliedActionCount: metadata.AppliedActionCount,
                    restoredActionCount: Math.Min(
                        metadata.AppliedActionCount,
                        recovery.RestoredCount),
                    conflictCount: recovery.ConflictCount,
                    errorCount: recovery.ErrorCount
                        + (status == SessionCompletionStatus.FailedBeforeApply
                            ? 1
                            : 0)),
                cancellationToken)
            .ConfigureAwait(false);
        await RecordCheckpointAsync(
                sessionId,
                SessionCheckpoint.ReconciliationComplete,
                metadata,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void StartMonitor(ActiveRuntime runtime)
    {
        _monitorTask = MonitorAsync(runtime, _lifetime.Token);
    }

    /// <summary>
    /// Starts watching for background processes that begin hogging the CPU
    /// after the session is already running. The one-off priority changes made
    /// at session start cover what was busy then; this covers what turns up
    /// later.
    /// </summary>
    private void StartProBalance(ActiveRuntime runtime, bool enabled)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            IProBalanceActuator actuator =
                _proBalanceActuatorFactory?.Invoke(runtime.SessionId)
                ?? new JournaledProBalanceActuator(
                    _journal,
                    runtime.SessionId,
                    _identityProvider,
                    _timeProvider,
                    ResolveBackgroundCpuSetIds(),
                    runtime.BackgroundAffinityMask,
                    _proBalanceSettings?.LowerBackgroundIoPriority
                        ?? new ProBalanceSettings().LowerBackgroundIoPriority);

            // Kazde ograniczenie, ktore petla nalozy, trafia do punktu
            // kontrolnego sesji. Bez tego wpisy w dzienniku sa, ale po
            // awarii hosta nikt ich nie czyta — proces zostawalby w cwiartce
            // maszyny z obnizonymi priorytetami na stale.
            if (actuator is IRestraintLedgerAware ledgerAware)
            {
                ledgerAware.AttachLedger(
                    new RuntimeRestraintLedger(this, runtime));
            }

            ProBalanceSupervisor supervisor = new(
                _cpuProcessSourceFactory(),
                actuator,
                () => runtime.ProcessTree
                    .GetKnownProcesses()
                    .Select(identity => identity.RuntimeKey.ProcessId)
                    .ToHashSet(),
                settings: _proBalanceSettings,
                timeProvider: _timeProvider);
            runtime.ProBalance = supervisor;
            supervisor.Start();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or UnauthorizedAccessException
                or IOException)
        {
            // Sesja ma dzialac takze wtedy, gdy ta czesc nie wstala.
            runtime.ProBalance = null;
        }
    }

    /// <summary>
    /// Stops the watcher and gives back every priority it lowered. Called
    /// before the session is cleared, so a failure here still leaves the
    /// journal able to finish the job on the next start.
    /// </summary>
    private static async ValueTask StopProBalanceAsync(ActiveRuntime runtime) =>
        await runtime.StopProBalanceOnceAsync(static async supervisor =>
            {
                try
                {
                    await supervisor.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or UnauthorizedAccessException
                        or IOException)
                {
                }
            })
            .ConfigureAwait(false);

    private async Task MonitorAsync(
        ActiveRuntime runtime,
        CancellationToken cancellationToken)
    {
        int missingObservations = 0;

        while (true)
        {
            if (!ReferenceEquals(_activeSession, runtime))
            {
                return;
            }

            GameProcessTreeObservation observation =
                await runtime.ProcessTree.ObserveAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!ReferenceEquals(_activeSession, runtime))
            {
                return;
            }

            if (observation.HasRunningProcess
                && runtime.FrameRateTrackingEnabled)
            {
                FrameRateSample sample =
                    await _frameRateProvider.SampleAsync(
                            observation.RunningProcesses
                                .Select(identity =>
                                    identity.RuntimeKey.ProcessId)
                                .ToArray(),
                            cancellationToken)
                        .ConfigureAwait(false);
                if (runtime.FrameRateTrackingEnabled)
                {
                    runtime.FrameRate = sample;
                }
                else
                {
                    await _frameRateProvider.StopAsync(cancellationToken)
                        .ConfigureAwait(false);
                    runtime.FrameRate = FrameRateSample.Disabled();
                }
            }
            else if (!runtime.FrameRateTrackingEnabled)
            {
                runtime.FrameRate = FrameRateSample.Disabled();
            }

            if (observation.NewlyDiscoveredCount > 0)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!ReferenceEquals(_activeSession, runtime))
                    {
                        return;
                    }

                    ClearInheritedCornerFromGame(
                        runtime,
                        observation.RunningProcesses);
                    await RecordRuntimeCheckpointAsync(
                            runtime,
                            SessionCheckpoint.GameProcessTreeObserved,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }

            if (observation.HasRunningProcess)
            {
                missingObservations = 0;
                await Task.Delay(_monitorInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!observation.IsReliable)
            {
                await Task.Delay(_monitorInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            missingObservations++;
            if (missingObservations < RequiredMissingProcessObservations)
            {
                await Task.Delay(_monitorInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_activeSession, runtime))
                {
                    return;
                }

                runtime.State = SessionStateMachine.Transition(
                    runtime.State,
                    OptimizationSessionState.GameExited);
                await CompleteRuntimeAsync(
                        runtime,
                        runtime.RecoveredFromHostCrash
                            ? SessionCompletionStatus.RecoveredAfterCrash
                            : SessionCompletionStatus.Completed,
                        "Gra została zamknięta; GameShift przywrócił aplikacje w tle.",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async ValueTask<bool> TryRequestGracefulGameCloseAsync(
        ActiveRuntime runtime,
        IReadOnlyList<ProcessIdentity> runningProcesses,
        CancellationToken cancellationToken)
    {
        ProcessIdentity[] candidates = runningProcesses
            .DistinctBy(identity => identity.RuntimeKey)
            .OrderByDescending(identity =>
                identity.RuntimeKey == runtime.RootProcess.RuntimeKey)
            .ThenByDescending(identity =>
                identity.RuntimeKey.StartedAtUtc)
            .ToArray();
        List<ProcessIdentity> interactiveDescendants = [];

        foreach (ProcessIdentity identity in candidates)
        {
            try
            {
                using Process process =
                    await ProcessTargetGuard.OpenValidatedAsync(
                            identity,
                            _identityProvider,
                            cancellationToken)
                    .ConfigureAwait(false);
                if (!ProcessWindowHelper.HasInteractiveWindow(process))
                {
                    continue;
                }

                if (identity.RuntimeKey == runtime.RootProcess.RuntimeKey)
                {
                    if (ProcessWindowHelper.RequestGracefulClose(process))
                    {
                        return true;
                    }

                    continue;
                }

                interactiveDescendants.Add(identity);
            }
            catch (Exception exception) when (
                exception is
                    InvalidOperationException
                    or UnauthorizedAccessException
                    or Win32Exception)
            {
            }
        }

        if (interactiveDescendants.Count > 1)
        {
            throw new InvalidOperationException(
                "Drzewo gry ma kilka niezależnych okien. GameShift nie "
                + "zgaduje, który proces zamknąć; zamknij grę z jej menu.");
        }

        if (interactiveDescendants.Count == 0)
        {
            return false;
        }

        using Process descendant =
            await ProcessTargetGuard.OpenValidatedAsync(
                    interactiveDescendants[0],
                    _identityProvider,
                    cancellationToken)
                .ConfigureAwait(false);
        return ProcessWindowHelper.RequestGracefulClose(descendant);
    }

    private async ValueTask<int> ForceTerminateVerifiedGameProcessesAsync(
        ActiveRuntime runtime,
        IReadOnlyList<ProcessIdentity> runningProcesses,
        CancellationToken cancellationToken)
    {
        ProcessIdentity[] candidates = runningProcesses
            .DistinctBy(identity => identity.RuntimeKey)
            .OrderByDescending(identity =>
                identity.RuntimeKey == runtime.RootProcess.RuntimeKey)
            .ThenByDescending(identity =>
                identity.RuntimeKey.StartedAtUtc)
            .ToArray();
        List<ProcessIdentity> interactive = [];
        ProcessIdentity? runningRoot = null;

        foreach (ProcessIdentity identity in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsProtectedGameSupportProcess(identity.ExecutablePath))
            {
                continue;
            }

            try
            {
                using Process process =
                    await ProcessTargetGuard.OpenValidatedAsync(
                            identity,
                            _identityProvider,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (ProcessWindowHelper.HasInteractiveWindow(process))
                {
                    interactive.Add(identity);
                }

                if (identity.RuntimeKey == runtime.RootProcess.RuntimeKey)
                {
                    runningRoot = identity;
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited between observation and the
                // explicit user request. It is not replaced with another PID.
            }
        }

        ProcessIdentity[] targets = interactive.Count > 0
            ? interactive.ToArray()
            : runningRoot is null
                ? []
                : [runningRoot];
        if (targets.Length == 0)
        {
            throw new InvalidOperationException(
                "Nie znaleziono zweryfikowanego procesu okna gry, który "
                + "można natychmiast zakończyć.");
        }

        int terminatedCount = 0;
        foreach (ProcessIdentity identity in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using Process process =
                    await ProcessTargetGuard.OpenValidatedAsync(
                            identity,
                            _identityProvider,
                            cancellationToken)
                        .ConfigureAwait(false);
                process.Kill(entireProcessTree: false);
                using CancellationTokenSource timeout =
                    new(ForcedGameExitTimeout);
                await process.WaitForExitAsync(timeout.Token)
                    .ConfigureAwait(false);
                terminatedCount++;
            }
            catch (ArgumentException)
            {
                terminatedCount++;
            }
            catch (InvalidOperationException exception)
                when (exception.InnerException is ArgumentException)
            {
                terminatedCount++;
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Windows nie zakończył procesu gry w wymaganym czasie.");
            }
        }

        return terminatedCount;
    }

    private static bool IsProtectedGameSupportProcess(
        string executablePath)
    {
        string processName = Path.GetFileNameWithoutExtension(
            executablePath);
        string[] protectedTerms =
        [
            "anticheat",
            "anti-cheat",
            "battleye",
            "beservice",
            "easyanticheat",
            "faceit",
            "pnkbstr",
            "punkbuster",
            "vgc",
            "vgtray",
        ];
        return protectedTerms.Any(term =>
            processName.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private async ValueTask<GameSessionSnapshot> CompleteRuntimeAsync(
        ActiveRuntime runtime,
        SessionCompletionStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        runtime.State = SessionStateMachine.Transition(
            runtime.State,
            OptimizationSessionState.Restoring);

        // Petla ograniczania staje pierwsza i oddaje, co wziela, zanim
        // zapiszemy poczatek przywracania — jej zwolnienia sa wtedy czescia
        // sesji, nie czyms po jej zamknieciu. To, czego nie zdola oddac,
        // zostaje w liscie ograniczen i przechodzi ta sama droga, co
        // zaplanowane aplikacje.
        await StopProBalanceAsync(runtime).ConfigureAwait(false);

        PresentMonCaptureCleanupException? frameRateCleanupFailure = null;
        try
        {
            await _frameRateProvider.StopAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (PresentMonCaptureCleanupException exception)
        {
            frameRateCleanupFailure = exception;
        }

        SessionRecoveryMetadata metadata =
            CreateRecoveryMetadata(runtime);
        await RecordCheckpointAsync(
                runtime.SessionId,
                SessionCheckpoint.RestoreStarted,
                metadata,
                CancellationToken.None)
            .ConfigureAwait(false);

        SystemGameProfileOperationResult systemProfileRecovery =
            runtime.SystemProfileActive
                ? await _systemProfileCoordinator.RestoreAsync(
                        runtime.ProfileId,
                        CancellationToken.None)
                    .ConfigureAwait(false)
                : SystemGameProfileOperationResult.Skipped(
                    "Zewnętrzny profil systemowy nie był aktywny.");
        if (systemProfileRecovery.Succeeded)
        {
            runtime.SystemProfileActive = false;
        }

        BackgroundRecoveryTotals priorityRecovery =
            await RestoreGamePriorityAsync(
                    runtime.SessionId,
                    runtime.GamePriority,
                    runtime.StartedAtUtc,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BackgroundRecoveryTotals restraintRecovery =
            await RestoreBackgroundApplicationsAsync(
                    runtime.SessionId,
                    runtime.SnapshotDynamicRestraints(),
                    runtime.StartedAtUtc,
                    runtime.BackgroundAffinityMask,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BackgroundRecoveryTotals applicationRecovery =
            await RestoreBackgroundApplicationsAsync(
                    runtime.SessionId,
                    runtime.BackgroundApplications,
                    runtime.StartedAtUtc,
                    runtime.BackgroundAffinityMask,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BackgroundRecoveryTotals recovery = MergeRecoveryTotals(
            MergeRecoveryTotals(priorityRecovery, restraintRecovery),
            applicationRecovery);
        if (recovery.ErrorCount == 0)
        {
            // Lista zostaje przy bledzie, zeby „Przywroc teraz" objelo takze
            // ograniczenia reaktywne. Powtorka jest idempotentna.
            runtime.ClearDynamicRestraints();
        }

        if (!systemProfileRecovery.Succeeded)
        {
            recovery = recovery with
            {
                ErrorCount = recovery.ErrorCount + 1,
            };
        }

        if (frameRateCleanupFailure is not null)
        {
            recovery = recovery with
            {
                ErrorCount = recovery.ErrorCount + 1,
            };
        }

        runtime.RestoredActionCount = Math.Min(
            runtime.AppliedActionCount,
            recovery.RestoredCount);
        runtime.ConflictCount = recovery.ConflictCount;
        runtime.ErrorCount = recovery.ErrorCount;

        runtime.State = SessionStateMachine.Transition(
            runtime.State,
            OptimizationSessionState.Reconciling);
        if (recovery.ErrorCount > 0)
        {
            runtime.State = SessionStateMachine.Transition(
                runtime.State,
                OptimizationSessionState.RecoveryRequired);
            return ToSnapshot(
                runtime,
                frameRateCleanupFailure is null
                    ? "Nie udało się przywrócić wszystkich aplikacji. "
                        + "GameShift zachował journal i ponowi recovery po "
                        + "wybraniu „Przywróć teraz” lub restarcie hosta."
                    : "Nie udało się potwierdzić zakończenia PresentMon. "
                        + "Pozostałe akcje zostały przywrócone, a GameShift "
                        + "zachował uchwyt i journal do bezpiecznej ponownej "
                        + "próby przez „Przywróć teraz”.");
        }

        await RecordCheckpointAsync(
                runtime.SessionId,
                SessionCheckpoint.ProcessesRestored,
                metadata,
                CancellationToken.None)
            .ConfigureAwait(false);
        DateTimeOffset endedAtUtc = _timeProvider.GetUtcNow();
        SessionCompletionStatus finalStatus =
            recovery.ConflictCount == 0
                ? status
                : SessionCompletionStatus.RestoredWithConflicts;
        await _history.AddAsync(
                new(
                    runtime.SessionId,
                    runtime.ProfileId,
                    runtime.GameDisplayName,
                    runtime.StartedAtUtc,
                    endedAtUtc,
                    finalStatus,
                    runtime.AppliedActionCount,
                    runtime.RestoredActionCount,
                    runtime.ConflictCount,
                    runtime.ErrorCount,
                    runtime.FrameRateStatistics),
                CancellationToken.None)
            .ConfigureAwait(false);
        await RecordCheckpointAsync(
                runtime.SessionId,
                SessionCheckpoint.ReconciliationComplete,
                metadata,
                CancellationToken.None)
            .ConfigureAwait(false);
        runtime.State = SessionStateMachine.Transition(
            runtime.State,
            recovery.ConflictCount == 0
                ? OptimizationSessionState.Completed
                : OptimizationSessionState.CompletedWithWarnings);
        ClearGameCpuSets(runtime);
        _activeSession = null;
        await TryRefreshCompletedSessionMetadataAsync(
                runtime.ProfileId,
                runtime.StartedAtUtc,
                endedAtUtc)
            .ConfigureAwait(false);
        return ToSnapshot(
            runtime,
            runtime.RestoredActionCount == 0
                ? message
                : $"{message} Przywrócono aplikacji: "
                    + $"{runtime.RestoredActionCount}.");
    }

    private async ValueTask TryRefreshCompletedSessionMetadataAsync(
        GameProfileId profileId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc)
    {
        if (_gameMetadataRepository is null
            || _metadataRefreshService is null)
        {
            return;
        }

        try
        {
            _ = await _metadataRefreshService.RefreshAfterSessionAsync(
                    _profiles,
                    _gameMetadataRepository,
                    profileId,
                    startedAtUtc,
                    endedAtUtc,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or DbException)
        {
            // Metadata is optional and must not reopen a restored session.
        }
    }

    private static SessionRecoveryMetadata CreateRecoveryMetadata(
        ActiveRuntime runtime) =>
        new(
            runtime.ProfileId,
            runtime.GameDisplayName,
            runtime.StartedAtUtc,
            runtime.RootProcess,
            runtime.ProcessTree.GetKnownProcesses(),
            runtime.BackgroundApplications
                .Select(ToRecoveryMetadata)
                .ToArray(),
            runtime.GamePriority,
            runtime.AppliedActionCount,
            runtime.FrameRateTrackingEnabled,
            runtime.SystemProfileActive,
            runtime.SnapshotDynamicRestraints()
                .Select(ToRecoveryMetadata)
                .ToArray(),
            runtime.BackgroundAffinityMask);

    /// <summary>
    /// Writes the session checkpoint that carries the current set of reactive
    /// restraints. Called from the restraint loop's thread, so it must not
    /// take the orchestrator's gate — the loop is stopped under that gate
    /// when the session closes, and its final releases land here. The journal
    /// serialises its own writes.
    /// </summary>
    private async ValueTask CheckpointRestraintsAsync(
        ActiveRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_activeSession, runtime))
        {
            return;
        }

        await RecordRuntimeCheckpointAsync(
                runtime,
                SessionCheckpoint.BackgroundRestraintChanged,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the runtime's snapshot and writes it as one indivisible step, so
    /// two writers cannot interleave a stale snapshot after a fresh one.
    /// </summary>
    private async ValueTask RecordRuntimeCheckpointAsync(
        ActiveRuntime runtime,
        SessionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await _checkpointGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RecordCheckpointAsync(
                    runtime.SessionId,
                    checkpoint,
                    CreateRecoveryMetadata(runtime),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    /// <summary>
    /// A reactive restraint in the shape of a planned application, so the
    /// planned-application restore path reverses it. The loop lowers priority
    /// with the same BelowNormal action the plan uses; the rest of the bundle
    /// is carried by the optional action ids.
    /// </summary>
    private static PlannedBackgroundApplication ToPlannedApplication(
        RestrainedProcessRecord record) =>
        new(
            record.PriorityActionId,
            record.PriorityIdempotencyKey,
            record.DisplayName,
            record.Identity,
            BackgroundProcessActionMode.LowerPriority,
            EcoQosActionId: null,
            EcoQosIdempotencyKey: null,
            record.AffinityActionId,
            record.AffinityIdempotencyKey,
            record.IoPriorityActionId,
            record.IoPriorityIdempotencyKey,
            record.MemoryPriorityActionId,
            record.MemoryPriorityIdempotencyKey,
            RestartDescriptor: null,
            EstimatedWorkingSetBytes: 0,
            AppliedAtUtc: record.RestrainedAtUtc,
            SteeredCpuSets: record.SteeredCpuSets);

    private GameProcessTreeSessionTracker CreateGameProcessTreeTracker(
        ProcessIdentity root,
        IEnumerable<ProcessIdentity>? previouslyTracked) =>
        new(
            root,
            previouslyTracked,
            _identityProvider,
            _processParentMapProvider);

    private async ValueTask FinalizeFailedLaunchAsync(
        PendingPlan plan,
        SessionRecoveryMetadata metadata,
        BackgroundRecoveryTotals recovery,
        CancellationToken cancellationToken)
    {
        await FinalizeRecoveredSessionAsync(
                plan.SessionId,
                metadata,
                SessionCompletionStatus.FailedBeforeApply,
                recovery,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<RecoveryJournalEntry> RecordCheckpointAsync(
        SessionId sessionId,
        SessionCheckpoint checkpoint,
        SessionRecoveryMetadata metadata,
        CancellationToken cancellationToken) =>
        _checkpointWriter.RecordAsync(
            sessionId,
            checkpoint,
            JsonSerializer.Serialize(metadata, SerializerOptions),
            cancellationToken);

    /// <summary>
    /// Pins the game to the machine's performance cores, when the machine has
    /// any. On a CPU whose cores are all the same speed this does nothing and
    /// says so — a fixed mask there only takes away the scheduler's freedom to
    /// react without moving the work anywhere better.
    /// <para>
    /// Applied through the same transaction machinery as everything else, so
    /// the original mask is journaled and put back when the session ends or
    /// after a crash. Failure is not fatal to the session: a game that runs
    /// without pinning is better than a session that refuses to start.
    /// </para>
    /// </summary>
    private async ValueTask<int> ApplyGameAffinityAsync(
        SessionId sessionId,
        ProcessIdentity gameIdentity,
        DateTimeOffset requestedAtUtc,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            return 0;
        }

        CpuTopology? topology = SystemCpuTopologyProvider.Read();
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            topology,
            CpuAffinityRole.Foreground);
        if (!decision.ShouldApply || topology is null)
        {
            return 0;
        }

        // Domyslne zbiory procesorow sa podpowiedzia, nie regula: harmonogram
        // trzyma gre na wskazanych rdzeniach, dopoki moze, i wychodzi poza nie,
        // gdy musi. Twarda maska tego nie potrafi i zbyt waska zaglodzi gre,
        // wiec zbiory ida pierwsze.
        //
        // Wyjatek: proces z juz zawezonym affinity ignoruje zbiory calkowicie,
        // a odczyt zwrotny i tak zwroci to, co zapisano. Wtedy jedyna droga,
        // ktora naprawde dziala, jest maska.
        if (TryApplyCpuSets(gameIdentity, topology, decision.Mask))
        {
            return 1;
        }

        ActionId actionId = new(Guid.NewGuid());
        ProcessAffinityAction action = new(
            actionId,
            gameIdentity,
            decision.Mask,
            _identityProvider);
        ActionExecutionContext context = new(
            sessionId,
            actionId,
            IdempotencyKey.Create(),
            requestedAtUtc);

        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<ProcessAffinityState>(_journal)
                    .ExecuteAsync(action, context, cancellationToken)
                    .ConfigureAwait(false);
            return result.Status is ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted
                ? 1
                : 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or Win32Exception
                or TimeoutException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The processors background work should be steered onto: the slow tier on
    /// a hybrid machine, or the cache groups the game is not in. Empty when the
    /// hardware gives no such split, in which case restraint stays a matter of
    /// priority alone.
    /// </summary>
    /// <summary>
    /// Mask a restrained background process is confined to, or zero when this
    /// machine does not qualify. Same policy as the CPU set list above, but
    /// used as a hard rule rather than a preference — which is the difference
    /// between the loop moving frame times and not.
    /// </summary>
    private static ulong ResolveBackgroundAffinityMask()
    {
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        return decision.ShouldApply ? decision.Mask : 0;
    }

    private static IReadOnlyList<uint> ResolveBackgroundCpuSetIds()
    {
        CpuTopology? topology = SystemCpuTopologyProvider.Read();
        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            topology,
            CpuAffinityRole.Background);
        if (!decision.ShouldApply || topology is null)
        {
            return [];
        }

        return [.. topology.Processors
            .Where(processor =>
                processor.LogicalProcessorIndex < 64
                && (decision.Mask
                    & (1UL << processor.LogicalProcessorIndex)) != 0)
            .Select(processor => processor.Id)];
    }

    /// <summary>
    /// Gives the game back the whole machine by clearing its default CPU sets.
    /// Cheap and safe to call when nothing was ever assigned.
    /// </summary>
    private static void ClearGameCpuSets(ActiveRuntime runtime) =>
        _ = ProcessCpuSets.TryApply(
            runtime.RootProcess.RuntimeKey.ProcessId,
            []);

    /// <summary>
    /// Installs default CPU sets matching the wanted mask. Returns false when
    /// the process cannot honour them, leaving the caller to fall back to a
    /// hard affinity mask.
    /// <para>
    /// Not journaled: clearing the assignment restores the machine-wide
    /// default, which is what the process had, and the session-end path clears
    /// it. A crash leaves a preference behind, not a restriction — the process
    /// keeps running on every core either way, and the assignment dies with the
    /// process.
    /// </para>
    /// </summary>
    private static bool TryApplyCpuSets(
        ProcessIdentity gameIdentity,
        CpuTopology topology,
        ulong desiredMask)
    {
        int processId = gameIdentity.RuntimeKey.ProcessId;
        ulong currentAffinity;
        try
        {
            using Process process = Process.GetProcessById(processId);
            currentAffinity = (ulong)process.ProcessorAffinity.ToInt64();
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception)
        {
            return false;
        }

        if (ProcessCpuSets.WouldBeDefeatedByAffinity(
                currentAffinity,
                desiredMask))
        {
            return false;
        }

        uint[] ids = [.. topology.Processors
            .Where(processor =>
                processor.LogicalProcessorIndex < 64
                && (desiredMask & (1UL << processor.LogicalProcessorIndex)) != 0)
            .Select(processor => processor.Id)];
        return ids.Length > 0 && ProcessCpuSets.TryApply(processId, ids);
    }

    private async ValueTask ApplyGamePriorityAsync(
        SessionId sessionId,
        PlannedGamePriority priority,
        ProcessIdentity gameIdentity,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken)
    {
        RuntimeProcessPriorityAction action = new(
            priority.ActionId,
            gameIdentity,
            priority.DesiredPriority);
        ActionExecutionContext context = new(
            sessionId,
            priority.ActionId,
            priority.IdempotencyKey,
            requestedAtUtc);
        ActionExecutionResult result =
            await new TransactionCoordinator<RuntimeProcessPriorityState>(
                    _journal)
                .ExecuteAsync(action, context, cancellationToken)
                .ConfigureAwait(false);
        if (result.Status is not (
            ActionExecutionStatus.AppliedAndVerified
            or ActionExecutionStatus.AlreadyCompleted))
        {
            throw new InvalidOperationException(
                "Nie ustawiono priorytetu gry: "
                + (result.Details
                    ?? "transakcja nie została zweryfikowana."));
        }
    }

    private async ValueTask<BackgroundRecoveryTotals>
        RestoreGamePriorityAsync(
            SessionId sessionId,
            GamePriorityRecoveryMetadata? priority,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken)
    {
        if (priority is null)
        {
            return new(0, 0, 0);
        }

        try
        {
            RuntimeProcessPriorityAction action = new(
                new ActionId(priority.ActionId),
                priority.Identity,
                priority.DesiredPriority);
            ActionExecutionContext context = new(
                sessionId,
                new ActionId(priority.ActionId),
                new IdempotencyKey(priority.IdempotencyKey),
                requestedAtUtc);
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<
                        RuntimeProcessPriorityState>(_journal)
                    .RecoverAsync(
                        action,
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
            return result.Status switch
            {
                ActionRecoveryStatus.Restored
                    or ActionRecoveryStatus.AlreadyRestored =>
                        new(1, 0, 0),
                ActionRecoveryStatus.NotRequired
                    or ActionRecoveryStatus.MissingPreparation =>
                        new(0, 0, 0),
                ActionRecoveryStatus.ConflictRequiresDecision =>
                        new(0, 1, 0),
                _ => new(0, 0, 1),
            };
        }
        catch (Exception exception) when (
            exception is
                IOException
                or InvalidDataException
                or InvalidOperationException
                or Win32Exception
                or UnauthorizedAccessException
                or TimeoutException)
        {
            return new(0, 0, 1);
        }
    }

    private async ValueTask<BackgroundBundleOutcome>
        ApplyBackgroundApplicationsAsync(
            SessionId sessionId,
            IReadOnlyList<PlannedBackgroundApplication> applications,
            DateTimeOffset requestedAtUtc,
            ulong backgroundMask,
            CancellationToken cancellationToken)
    {
        int appliedCount = 0;
        int confinedCount = 0;
        foreach (PlannedBackgroundApplication application in applications)
        {
            ActionExecutionContext context =
                CreateActionContext(
                    sessionId,
                    application,
                    requestedAtUtc);
            if (application.ActionMode
                == BackgroundProcessActionMode.CloseAndRestore)
            {
                GracefulCloseApplicationAction action =
                    CreateBackgroundApplicationAction(application);
                ActionExecutionResult closeResult =
                    await new TransactionCoordinator<
                            ApplicationProcessState>(_journal)
                        .ExecuteAsync(
                            action,
                            context,
                            cancellationToken)
                        .ConfigureAwait(false);
                EnsureActionApplied(
                    closeResult,
                    application.DisplayName,
                    "bezpieczne zamknięcie");
                appliedCount++;
                continue;
            }

            RuntimeProcessPriorityAction priorityAction = new(
                application.ActionId,
                application.Identity,
                ProcessPriorityClass.BelowNormal);
            ActionExecutionResult priorityResult =
                await new TransactionCoordinator<
                        RuntimeProcessPriorityState>(_journal)
                    .ExecuteAsync(
                        priorityAction,
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
            EnsureActionApplied(
                priorityResult,
                application.DisplayName,
                "obniżenie priorytetu");
            appliedCount++;

            if (application.ActionMode is not (
                BackgroundProcessActionMode.LowerPriorityAndEcoQos
                or BackgroundProcessActionMode.RestrainBackground))
            {
                continue;
            }

            RuntimeProcessEcoQosAction ecoQosAction = new(
                application.EcoQosActionId
                    ?? throw new InvalidDataException(
                        "Brakuje identyfikatora akcji EcoQoS."),
                application.Identity);
            ActionExecutionResult ecoQosResult =
                await new TransactionCoordinator<
                        RuntimeProcessEcoQosState>(_journal)
                    .ExecuteAsync(
                        ecoQosAction,
                        CreateEcoQosActionContext(
                            sessionId,
                            application,
                            requestedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            EnsureActionApplied(
                ecoQosResult,
                application.DisplayName,
                "włączenie EcoQoS");
            appliedCount++;

            // Reszta pakietu idzie po priorytecie i EcoQoS i jest
            // best-effort: nieudane nalozenie nie przewraca sesji, to, co juz
            // nalozone, zostaje. Kolejnosc od najmocniejszej dzwigni: maska
            // (jedyna, ktora w pomiarze ruszyla czas klatki, gdy priorytet
            // nie ruszal), potem pamiec, potem dysk. Maska jest najmniej
            // odporna na stan procesu — proces, ktory sam zawezil sobie
            // affinity, jej nie przyjmie — i wlasnie dlatego nie moze byc
            // warunkiem powodzenia calej sesji.
            if (backgroundMask != 0
                && application.AffinityActionId is { } affinityActionId
                && await TryApplyOptionalActionAsync(
                        new ProcessAffinityAction(
                            affinityActionId,
                            application.Identity,
                            backgroundMask,
                            _identityProvider),
                        CreateOptionalActionContext(
                            sessionId,
                            affinityActionId,
                            application.AffinityIdempotencyKey,
                            requestedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                appliedCount++;
                confinedCount++;
            }

            if (application.MemoryPriorityActionId is { } memoryPriorityActionId
                && await TryApplyOptionalActionAsync(
                        new ProcessMemoryPriorityAction(
                            memoryPriorityActionId,
                            application.Identity,
                            identityProvider: _identityProvider),
                        CreateOptionalActionContext(
                            sessionId,
                            memoryPriorityActionId,
                            application.MemoryPriorityIdempotencyKey,
                            requestedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                appliedCount++;
            }

            if (application.IoPriorityActionId is { } ioPriorityActionId
                && await TryApplyOptionalActionAsync(
                        new ProcessIoPriorityAction(
                            ioPriorityActionId,
                            application.Identity,
                            IoPriorityNativeMethods.IoPriorityVeryLow,
                            _identityProvider),
                        CreateOptionalActionContext(
                            sessionId,
                            ioPriorityActionId,
                            application.IoPriorityIdempotencyKey,
                            requestedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                appliedCount++;
            }
        }

        return new(appliedCount, confinedCount);
    }

    /// <summary>
    /// Executes one optional action of the background bundle through the
    /// journal. Optional means the session does not depend on it: a process
    /// that has already narrowed its own affinity, already lowered its own
    /// priorities, or changed identity keeps whatever it already received
    /// instead of failing the start. When it does apply, it is journaled like
    /// everything else and reversed by <see cref="RestoreOptionalActionAsync{TState}"/>.
    /// </summary>
    private async ValueTask<bool> TryApplyOptionalActionAsync<TState>(
        IReversibleAction<TState> action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
        where TState : notnull
    {
        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<TState>(_journal)
                    .ExecuteAsync(action, context, cancellationToken)
                    .ConfigureAwait(false);
            return result.Status is ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted;
        }
        catch (Exception exception) when (
            IsExpectedRecoveryFailure(exception))
        {
            return false;
        }
    }

    private async ValueTask<BackgroundRecoveryTotals>
        RestoreBackgroundApplicationsAsync(
            SessionId sessionId,
            IReadOnlyList<PlannedBackgroundApplication> applications,
            DateTimeOffset requestedAtUtc,
            ulong backgroundMask,
            CancellationToken cancellationToken)
    {
        BackgroundRecoveryTotals totals = new(0, 0, 0);

        // Rodzice, po ktorych trzeba jeszcze przejrzec potomkow: maska,
        // priorytet wejscia-wyjscia, priorytet pamieci i klasa BelowNormal
        // dziedzicza sie na procesy urodzone pod ograniczeniem, a dziennik
        // zna tylko rodzica. Kazdy ograniczony proces jest korzeniem, bo
        // klase dziedziczy dziecko kazdego z nich.
        List<SweepRoot> sweepRoots = [];

        foreach (PlannedBackgroundApplication application
                     in applications.Reverse())
        {
            // Pakiet odwracany w kolejnosci odwrotnej do nakladania: dysk,
            // pamiec, maska, EcoQoS, priorytet. Kazde odtworzenie jest
            // niezalezne i idempotentne, wiec kolejnosc nie jest krytyczna,
            // ale trzyma dziennik czytelnym.
            if (application.IoPriorityActionId is { } ioPriorityActionId)
            {
                totals = MergeRecoveryTotals(
                    totals,
                    await RestoreOptionalActionAsync<ProcessIoPriorityState>(
                            application,
                            () => new ProcessIoPriorityAction(
                                ioPriorityActionId,
                                application.Identity,
                                IoPriorityNativeMethods.IoPriorityVeryLow,
                                _identityProvider),
                            CreateOptionalActionContext(
                                sessionId,
                                ioPriorityActionId,
                                application.IoPriorityIdempotencyKey,
                                requestedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            if (application.MemoryPriorityActionId is { } memoryPriorityActionId)
            {
                totals = MergeRecoveryTotals(
                    totals,
                    await RestoreOptionalActionAsync<
                            ProcessMemoryPriorityState>(
                            application,
                            () => new ProcessMemoryPriorityAction(
                                memoryPriorityActionId,
                                application.Identity,
                                identityProvider: _identityProvider),
                            CreateOptionalActionContext(
                                sessionId,
                                memoryPriorityActionId,
                                application.MemoryPriorityIdempotencyKey,
                                requestedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            if (application.AffinityActionId is { } affinityActionId)
            {
                // Odtwarzanie nie potrzebuje maski: cofa do stanu z dziennika.
                // Dlatego nie zalezy od tego, czy maszyna kwalifikuje sie do
                // maski DZISIAJ — po awarii liczy sie to, co nalozono.
                totals = MergeRecoveryTotals(
                    totals,
                    await RestoreOptionalActionAsync<ProcessAffinityState>(
                            application,
                            () => ProcessAffinityAction.ForRecovery(
                                affinityActionId,
                                application.Identity,
                                _identityProvider),
                            CreateOptionalActionContext(
                                sessionId,
                                affinityActionId,
                                application.AffinityIdempotencyKey,
                                requestedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            if (application.ActionMode
                != BackgroundProcessActionMode.CloseAndRestore)
            {
                if (application.SteeredCpuSets)
                {
                    await ClearSteeredCpuSetsAsync(
                            application,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                // Granica czasu: chwila nalozenia tego konkretnego
                // ograniczenia, gdy jest znana, a nie poczatek sesji.
                // Potomek urodzony miedzy startem sesji a ograniczeniem
                // reaktywnym rodzica niczego po nim nie odziedziczyl.
                sweepRoots.Add(new(
                    application.Identity.RuntimeKey.ProcessId,
                    application.Identity.RuntimeKey.StartedAtUtc,
                    application.AppliedAtUtc ?? requestedAtUtc,
                    application.AffinityActionId is not null,
                    application.IoPriorityActionId is not null,
                    application.MemoryPriorityActionId is not null));
            }

            if (application.ActionMode is
                BackgroundProcessActionMode.LowerPriorityAndEcoQos
                or BackgroundProcessActionMode.RestrainBackground)
            {
                BackgroundRecoveryTotals ecoQosRecovery =
                    await RestoreEcoQosActionAsync(
                            sessionId,
                            application,
                            requestedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);
                totals = MergeRecoveryTotals(totals, ecoQosRecovery);
            }

            BackgroundRecoveryTotals primaryRecovery =
                await RestorePrimaryBackgroundActionAsync(
                        sessionId,
                        application,
                        requestedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            totals = MergeRecoveryTotals(totals, primaryRecovery);
        }

        // Potomkowie dopiero po przywroceniu rodzicow i z migawka zrobiona
        // TERAZ: dziecko urodzone w trakcie przywracania dziedziczylo jeszcze
        // cwiartke, a w migawce sprzed przywracania by go nie bylo. Potomek,
        // ktorego nie udalo sie oddac, liczy sie jako blad odtwarzania —
        // sesja zostaje otwarta do ponownej proby, zamiast udawac, ze po
        // GameShifcie nic nie zostalo.
        foreach (SweepRoot root in sweepRoots)
        {
            // Maska tylko dla korzenia, ktory ja dostal. Podanie jej dla
            // korzenia bez maski (tryb EcoQoS albo samego priorytetu)
            // otwieraloby regule sierot: kazdy obcy proces z cwiartka
            // bylby poszerzany na podstawie ograniczenia, ktorego nie bylo.
            InheritedRestraintSweep sweep = InheritedRestraintSweeper.Release(
                root.ProcessId,
                root.StartedAtUtc,
                TryCaptureParentMap,
                root.NotBeforeUtc,
                root.Mask ? backgroundMask : 0,
                root.Io,
                root.Memory,
                resetPriorityClass: true);
            if (sweep.SnapshotFailed)
            {
                // Bez migawki drzewa nie wiemy, czy cos zostalo. To jest
                // blad odtwarzania, nie sukces: sesja zostaje otwarta do
                // ponownej proby, zamiast udawac, ze po GameShifcie nic nie
                // zostalo.
                totals = totals with
                {
                    ErrorCount = totals.ErrorCount + 1,
                };
                break;
            }

            if (sweep.Failed > 0)
            {
                totals = totals with
                {
                    ErrorCount = totals.ErrorCount + sweep.Failed,
                };
            }
        }

        return totals;
    }

    /// <summary>
    /// Takes the default CPU sets off a process the reactive loop steered.
    /// Best-effort and not counted as an error: sets are a preference, not a
    /// restriction, and a process that is gone has nothing to clear. The
    /// identity check keeps a recycled id from having its sets touched.
    /// </summary>
    private async ValueTask ClearSteeredCpuSetsAsync(
        PlannedBackgroundApplication application,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _identityProvider
                    .MatchesRuntimeIdentityAsync(
                        application.Identity,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                _ = ProcessCpuSets.TryApply(
                    application.Identity.RuntimeKey.ProcessId,
                    []);
            }
        }
        catch (Exception exception) when (
            IsExpectedRecoveryFailure(exception))
        {
        }
    }

    private sealed record SweepRoot(
        int ProcessId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset NotBeforeUtc,
        bool Mask,
        bool Io,
        bool Memory);

    private async ValueTask<BackgroundRecoveryTotals>
        RestorePrimaryBackgroundActionAsync(
            SessionId sessionId,
            PlannedBackgroundApplication application,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken)
    {
        try
        {
            ActionRecoveryResult result;
            if (application.ActionMode
                == BackgroundProcessActionMode.CloseAndRestore)
            {
                GracefulCloseApplicationAction action =
                    CreateBackgroundApplicationAction(application);
                result =
                    await new ActionRecoveryCoordinator<
                            ApplicationProcessState>(_journal)
                        .RecoverAsync(
                            action,
                            CreateActionContext(
                                sessionId,
                                application,
                                requestedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                RuntimeProcessPriorityAction action = new(
                    application.ActionId,
                    application.Identity,
                    ProcessPriorityClass.BelowNormal);
                result =
                    await new ActionRecoveryCoordinator<
                            RuntimeProcessPriorityState>(_journal)
                        .RecoverAsync(
                            action,
                            CreateActionContext(
                                sessionId,
                                application,
                                requestedAtUtc),
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            return ToRecoveryTotals(result);
        }
        catch (Exception exception) when (
            IsExpectedRecoveryFailure(exception))
        {
            return new(0, 0, 1);
        }
    }

    /// <summary>
    /// A game process must never sit in the background corner. It can get
    /// there by inheritance: a launcher the user approved as background,
    /// confined at session start, later starts the actual game. The tree
    /// tracker notices the new process; this hands it the whole machine
    /// before anything else happens. Best-effort and silent — the game runs
    /// either way, only slower if this fails.
    /// </summary>
    private void ClearInheritedCornerFromGame(
        ActiveRuntime runtime,
        IReadOnlyList<ProcessIdentity> gameProcesses)
    {
        ulong corner = runtime.BackgroundAffinityMask;
        if (corner == 0)
        {
            return;
        }

        ulong machine = InheritedRestraintSweeper.MachineMask();
        if (machine == 0 || machine == corner)
        {
            return;
        }

        // Tylko proces, ktory mogl cwiartke odziedziczyc: urodzony w trakcie
        // tej sesji, po rodzicu, ktorego my zamknelismy w cwiartce
        // (zatwierdzona aplikacja tla albo ograniczenie reaktywne), albo po
        // innym procesie gry z tego samego lancucha. Sama rownosc maski nie
        // wystarcza — gra moze wybrac sobie maske sama, a jej wybor nie jest
        // nasza sprawa.
        HashSet<int> confinedParents = [.. runtime.BackgroundApplications
            .Concat(runtime.SnapshotDynamicRestraints())
            .Where(application => application.AffinityActionId is not null)
            .Select(application => application.Identity.RuntimeKey.ProcessId)];
        if (confinedParents.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<int, int>? parents = TryCaptureParentMap();
        if (parents is null)
        {
            return;
        }

        HashSet<int> gameProcessIds = [.. gameProcesses
            .Select(identity => identity.RuntimeKey.ProcessId)];
        foreach (ProcessIdentity identity in gameProcesses
            .OrderBy(identity => identity.RuntimeKey.StartedAtUtc))
        {
            if (identity.RuntimeKey.StartedAtUtc < runtime.StartedAtUtc
                || !parents.TryGetValue(
                    identity.RuntimeKey.ProcessId,
                    out int parentProcessId)
                || (!confinedParents.Contains(parentProcessId)
                    && !gameProcessIds.Contains(parentProcessId)))
            {
                continue;
            }

            try
            {
                using Process process = Process.GetProcessById(
                    identity.RuntimeKey.ProcessId);
                if ((ulong)process.ProcessorAffinity.ToInt64() == corner)
                {
                    process.ProcessorAffinity = (nint)(long)machine;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or InvalidOperationException
                    or Win32Exception)
            {
            }
        }
    }

    /// <summary>
    /// The mask a session actually imposed, read back from the journal when
    /// the checkpoint predates the field that records it. Today's topology
    /// is not evidence about a historical change: a machine that fails the
    /// policy now may still have processes confined by a session that ran
    /// before, and the reverse. Null when no affinity action left a
    /// preparation record — then nothing was confined.
    /// </summary>
    private static ulong? ReadHistoricalBackgroundMask(
        IReadOnlyList<RecoveryJournalEntry> entries,
        Guid sessionId,
        IEnumerable<PlannedBackgroundApplication> applications)
    {
        HashSet<Guid> affinityActionIds = [.. applications
            .Select(application => application.AffinityActionId)
            .OfType<ActionId>()
            .Select(actionId => actionId.Value)];
        if (affinityActionIds.Count == 0)
        {
            return null;
        }

        foreach (RecoveryJournalEntry entry in entries)
        {
            if (entry.SessionId != sessionId
                || entry.ActionId is not Guid actionId
                || !affinityActionIds.Contains(actionId)
                || entry.DesiredStateJson is null)
            {
                continue;
            }

            try
            {
                ProcessAffinityState? desired =
                    JsonSerializer.Deserialize<ProcessAffinityState>(
                        entry.DesiredStateJson,
                        JournalStateSerializerOptions);
                if (desired is { Mask: not 0 })
                {
                    return desired.Mask;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Same options the transaction machinery serializes action states with,
    /// so a state written by <c>TransactionCoordinator</c> reads back here.
    /// </summary>
    private static readonly JsonSerializerOptions JournalStateSerializerOptions =
        new(JsonSerializerDefaults.General);

    private IReadOnlyDictionary<int, int>? TryCaptureParentMap()
    {
        try
        {
            return _processParentMapProvider.Capture();
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reverses one optional action of the background bundle through the
    /// journal. A process that has exited on its own is skipped outright:
    /// every setting in the bundle dies with the process, so there is nothing
    /// to put back, and reading state from a process that no longer exists
    /// would count as a recovery error and end an otherwise clean session in
    /// the "recovery required" state. The plan promises the user exactly
    /// this: if the process ends by itself, do nothing.
    /// </summary>
    private async ValueTask<BackgroundRecoveryTotals>
        RestoreOptionalActionAsync<TState>(
            PlannedBackgroundApplication application,
            Func<IReversibleAction<TState>> actionFactory,
            ActionExecutionContext context,
            CancellationToken cancellationToken)
        where TState : notnull
    {
        try
        {
            if (!await _identityProvider
                    .MatchesRuntimeIdentityAsync(
                        application.Identity,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return new(0, 0, 0);
            }

            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<TState>(_journal)
                    .RecoverAsync(
                        actionFactory(),
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);
            return ToRecoveryTotals(result);
        }
        catch (Exception exception) when (
            IsExpectedRecoveryFailure(exception))
        {
            return new(0, 0, 1);
        }
    }

    private async ValueTask<BackgroundRecoveryTotals>
        RestoreEcoQosActionAsync(
            SessionId sessionId,
            PlannedBackgroundApplication application,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken)
    {
        try
        {
            RuntimeProcessEcoQosAction action = new(
                application.EcoQosActionId
                    ?? throw new InvalidDataException(
                        "Brakuje identyfikatora akcji EcoQoS."),
                application.Identity);
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<
                        RuntimeProcessEcoQosState>(_journal)
                    .RecoverAsync(
                        action,
                        CreateEcoQosActionContext(
                            sessionId,
                            application,
                            requestedAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            return ToRecoveryTotals(result);
        }
        catch (Exception exception) when (
            IsExpectedRecoveryFailure(exception))
        {
            return new(0, 0, 1);
        }
    }

    private static GracefulCloseApplicationAction
        CreateBackgroundApplicationAction(
            PlannedBackgroundApplication application) =>
        new(
            application.ActionId,
            application.Identity,
            application.RestartDescriptor
                ?? throw new InvalidDataException(
                    "Brakuje deskryptora przywrócenia aplikacji."));

    private static ActionExecutionContext CreateActionContext(
        SessionId sessionId,
        PlannedBackgroundApplication application,
        DateTimeOffset requestedAtUtc) =>
        new(
            sessionId,
            application.ActionId,
            application.IdempotencyKey,
            requestedAtUtc);

    private static ActionExecutionContext CreateEcoQosActionContext(
        SessionId sessionId,
        PlannedBackgroundApplication application,
        DateTimeOffset requestedAtUtc) =>
        new(
            sessionId,
            application.EcoQosActionId
                ?? throw new InvalidDataException(
                    "Brakuje identyfikatora akcji EcoQoS."),
            application.EcoQosIdempotencyKey
                ?? throw new InvalidDataException(
                    "Brakuje klucza idempotencji EcoQoS."),
            requestedAtUtc);

    private static ActionExecutionContext CreateOptionalActionContext(
        SessionId sessionId,
        ActionId actionId,
        IdempotencyKey? idempotencyKey,
        DateTimeOffset requestedAtUtc) =>
        new(
            sessionId,
            actionId,
            idempotencyKey
                ?? throw new InvalidDataException(
                    "Brakuje klucza idempotencji akcji tła."),
            requestedAtUtc);

    private static void EnsureActionApplied(
        ActionExecutionResult result,
        string displayName,
        string operation)
    {
        if (result.Status is
            ActionExecutionStatus.AppliedAndVerified
            or ActionExecutionStatus.AlreadyCompleted)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Nie wykonano działania „{operation}” dla "
            + $"„{displayName}”: "
            + (result.Details
                ?? "bezpieczna transakcja nie została zweryfikowana."));
    }

    private static BackgroundRecoveryTotals ToRecoveryTotals(
        ActionRecoveryResult result) =>
        result.Status switch
        {
            ActionRecoveryStatus.Restored
                or ActionRecoveryStatus.AlreadyRestored =>
                    new(1, 0, 0),
            ActionRecoveryStatus.NotRequired
                or ActionRecoveryStatus.MissingPreparation =>
                    new(0, 0, 0),
            ActionRecoveryStatus.ConflictRequiresDecision =>
                    new(0, 1, 0),
            _ => new(0, 0, 1),
        };

    private static bool IsExpectedRecoveryFailure(Exception exception) =>
        exception is
            IOException
            or InvalidDataException
            or InvalidOperationException
            or Win32Exception
            or UnauthorizedAccessException
            or TimeoutException;

    private static BackgroundApplicationRecoveryMetadata ToRecoveryMetadata(
        PlannedBackgroundApplication application) =>
        new(
            application.ActionId.Value,
            application.IdempotencyKey.Value,
            application.DisplayName,
            application.Identity,
            application.ActionMode,
            application.EcoQosActionId?.Value,
            application.EcoQosIdempotencyKey?.Value,
            application.RestartDescriptor?.WorkingDirectory,
            application.RestartDescriptor?.Arguments.ToArray(),
            application.EstimatedWorkingSetBytes,
            application.AffinityActionId?.Value,
            application.AffinityIdempotencyKey?.Value,
            application.IoPriorityActionId?.Value,
            application.IoPriorityIdempotencyKey?.Value,
            application.MemoryPriorityActionId?.Value,
            application.MemoryPriorityIdempotencyKey?.Value,
            application.AppliedAtUtc,
            application.SteeredCpuSets);

    private static GamePriorityRecoveryMetadata ToRecoveryMetadata(
        PlannedGamePriority priority,
        ProcessIdentity identity) =>
        new(
            priority.ActionId.Value,
            priority.IdempotencyKey.Value,
            identity,
            priority.DesiredPriority);

    private static PlannedBackgroundApplication FromRecoveryMetadata(
        BackgroundApplicationRecoveryMetadata metadata)
    {
        BackgroundProcessActionMode actionMode =
            metadata.ActionMode == 0
                ? BackgroundProcessActionMode.CloseAndRestore
                : metadata.ActionMode;
        if (!Enum.IsDefined(actionMode))
        {
            throw new InvalidDataException(
                "Journal zawiera nieobsługiwany tryb procesu tła.");
        }

        ActionId? ecoQosActionId = metadata.EcoQosActionId is Guid actionId
            ? new ActionId(actionId)
            : null;
        IdempotencyKey? ecoQosIdempotencyKey =
            metadata.EcoQosIdempotencyKey is Guid idempotencyKey
                ? new IdempotencyKey(idempotencyKey)
                : null;
        bool requiresEcoQos =
            actionMode is BackgroundProcessActionMode.LowerPriorityAndEcoQos
                or BackgroundProcessActionMode.RestrainBackground;
        if (requiresEcoQos
            != (ecoQosActionId is not null
                && ecoQosIdempotencyKey is not null))
        {
            throw new InvalidDataException(
                "Journal zawiera niespójne identyfikatory akcji EcoQoS.");
        }

        // Reszta pakietu jest opcjonalna takze dla trybu z EcoQoS: dzienniki
        // sprzed jej wprowadzenia nie maja tych identyfikatorow, a
        // odtwarzanie ma je czytac bez bledu. Brak identyfikatora znaczy
        // tylko tyle, ze danej dzwigni nigdy nie nalozono.
        (ActionId? affinityActionId, IdempotencyKey? affinityIdempotencyKey) =
            ReadOptionalAction(
                metadata.AffinityActionId,
                metadata.AffinityIdempotencyKey,
                "powinowactwa");
        (ActionId? ioPriorityActionId, IdempotencyKey? ioPriorityIdempotencyKey) =
            ReadOptionalAction(
                metadata.IoPriorityActionId,
                metadata.IoPriorityIdempotencyKey,
                "priorytetu wejścia-wyjścia");
        (ActionId? memoryPriorityActionId,
            IdempotencyKey? memoryPriorityIdempotencyKey) =
            ReadOptionalAction(
                metadata.MemoryPriorityActionId,
                metadata.MemoryPriorityIdempotencyKey,
                "priorytetu pamięci");

        return new(
            new ActionId(metadata.ActionId),
            new IdempotencyKey(metadata.IdempotencyKey),
            metadata.DisplayName,
            metadata.Identity,
            actionMode,
            ecoQosActionId,
            ecoQosIdempotencyKey,
            affinityActionId,
            affinityIdempotencyKey,
            ioPriorityActionId,
            ioPriorityIdempotencyKey,
            memoryPriorityActionId,
            memoryPriorityIdempotencyKey,
            actionMode
                    == BackgroundProcessActionMode.CloseAndRestore
                ? new ApplicationRestartDescriptor(
                    metadata.Identity.ExecutablePath,
                    metadata.WorkingDirectory
                        ?? throw new InvalidDataException(
                            "Brakuje katalogu restartu aplikacji."),
                    metadata.Arguments ?? [],
                    ApplicationRestartability.Restartable)
                : null,
            metadata.EstimatedWorkingSetBytes,
            metadata.RestrainedAtUtc,
            metadata.SteeredCpuSets);
    }

    /// <summary>
    /// One optional action's identifiers out of the journal. Both halves are
    /// present or both are absent; anything else is a corrupted record and
    /// is refused rather than half-recovered.
    /// </summary>
    private static (ActionId? ActionId, IdempotencyKey? IdempotencyKey)
        ReadOptionalAction(
            Guid? actionId,
            Guid? idempotencyKey,
            string actionName)
    {
        if ((actionId is null) != (idempotencyKey is null))
        {
            throw new InvalidDataException(
                $"Journal zawiera niespójne identyfikatory akcji {actionName}.");
        }

        return actionId is Guid id && idempotencyKey is Guid key
            ? (new ActionId(id), new IdempotencyKey(key))
            : (null, null);
    }

    private static string FormatMemory(long bytes)
    {
        double mebibytes = Math.Max(0, bytes) / 1024d / 1024d;
        return mebibytes >= 1024
            ? $"{mebibytes / 1024:0.0} GB"
            : $"{mebibytes:0} MB";
    }

    private static string DescribePriority(
        PlannedGamePriority? priority) =>
        priority?.DesiredPriority switch
        {
            ProcessPriorityClass.AboveNormal => "powyżej normalnego",
            ProcessPriorityClass.High => "wysoki",
            _ => "normalny",
        };

    private static string DescribeSavedRules(
        SavedRulePlanSummary? summary) =>
        summary is null
            ? string.Empty
            : " Zapisane reguły — skonfigurowane: "
                + $"{summary.ConfiguredRuleCount}; aktywne ścieżki: "
                + $"{summary.MatchedRuleCount}; dopasowane procesy: "
                + $"{summary.MatchedProcessCount}; zatwierdzone działania: "
                + $"{summary.ApprovedProcessCount}.";

    private static BackgroundRecoveryTotals MergeRecoveryTotals(
        BackgroundRecoveryTotals first,
        BackgroundRecoveryTotals second) =>
        new(
            first.RestoredCount + second.RestoredCount,
            first.ConflictCount + second.ConflictCount,
            first.ErrorCount + second.ErrorCount);

    /// <summary>
    /// The clause the plan preview adds when this machine qualifies for the
    /// background corner, so what the user approves is what will happen.
    /// Empty when the policy declines and no mask will be applied.
    /// </summary>
    private static string DescribeBackgroundCorner(
        CpuAffinityDecision decision) =>
        decision.ShouldApply
            ? " i przypnij do "
                + $"{System.Numerics.BitOperations.PopCount(decision.Mask)} "
                + "wątków tła, żeby nie dzielił rdzeni z grą"
            : string.Empty;

    private static SessionPlanPreview ToPreview(PendingPlan plan)
    {
        List<SessionPlanItem> items = [];
        CpuAffinityDecision backgroundCorner = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Background);
        if (plan.SavedRuleSummary is not null)
        {
            items.Add(
                new(
                    "SAVED_RULES_RESOLVED",
                    "Szybki Play używa wyłącznie zapisanych reguł tej gry. "
                    + DescribeSavedRules(plan.SavedRuleSummary).Trim(),
                    "Niskie",
                    "Każde dopasowanie nadal przechodzi pełną walidację "
                    + "tożsamości i standardowe przywracanie."));
        }

        foreach (PlannedBackgroundApplication application
                     in plan.BackgroundApplications)
        {
            items.Add(
                application.ActionMode switch
                {
                    BackgroundProcessActionMode.CloseAndRestore => new(
                        "CLOSE_BACKGROUND_APP",
                        $"Zamknij „{application.DisplayName}” zwykłym "
                        + $"komunikatem Windows i zwolnij około "
                        + $"{FormatMemory(application.EstimatedWorkingSetBytes)} RAM.",
                        "Niskie",
                        "Po zakończeniu gry uruchom ponownie ten sam, "
                        + "zweryfikowany plik EXE."),
                    BackgroundProcessActionMode.RestrainBackground => new(
                        "RESTRAIN_BACKGROUND",
                        $"Na czas gry ustaw „{application.DisplayName}” "
                        + "na BelowNormal, włącz EcoQoS"
                        + DescribeBackgroundCorner(backgroundCorner)
                        + " i obniż jej priorytet pamięci oraz "
                        + "wejścia-wyjścia, aby tło ustępowało grze na "
                        + "procesorze, w pamięci i na dysku. Zysk w czasie "
                        + "klatki zmierzono tylko dla przypięcia i tylko "
                        + "pod obciążeniem; reszta to dźwignie odwracalne "
                        + "bez zmierzonego zysku.",
                        "Niskie",
                        "Przywróć poprzedni priorytet, stan EcoQoS, "
                        + "przypisanie rdzeni oraz priorytety pamięci "
                        + "i wejścia-wyjścia, także procesom potomnym, "
                        + "które je odziedziczyły; jeśli proces sam się "
                        + "zakończy, nie rób nic."),
                    BackgroundProcessActionMode
                            .LowerPriorityAndEcoQos => new(
                        "LIMIT_BACKGROUND_CPU",
                        $"Na czas gry ustaw „{application.DisplayName}” "
                        + "na BelowNormal i włącz EcoQoS, aby ograniczyć "
                        + "rywalizację tła o CPU. Ten plan nie przypina do "
                        + "rdzeni i nie zmienia priorytetów pamięci ani dysku; "
                        + "niezależnie od planu włączona pętla ProBalance może "
                        + "ograniczyć każdy proces, który w trakcie gry "
                        + "zacznie zjadać procesor.",
                        "Niskie",
                        "Przywróć poprzedni priorytet i stan EcoQoS; "
                        + "jeśli proces sam się zakończy, nie rób nic."),
                    _ => new(
                        "LOWER_BACKGROUND_PRIORITY",
                        $"Na czas gry ustaw „{application.DisplayName}” "
                        + "na BelowNormal, aby ograniczyć rywalizację o CPU.",
                        "Niskie",
                        "Przywróć dokładnie poprzednią klasę priorytetu; "
                        + "jeśli proces sam się zakończy, nie rób nic."),
                });
        }
        if (plan.GamePriority is not null)
        {
            items.Add(
                new(
                    "BOOST_GAME_PRIORITY",
                    $"Ustaw przez API Windows priorytet gry na "
                    + $"{DescribePriority(plan.GamePriority)} i odczytaj "
                    + "go ponownie z procesu.",
                    plan.GamePriority.DesiredPriority
                        == ProcessPriorityClass.High
                            ? "Podwyższone"
                            : "Niskie",
                    "Przy ręcznym zakończeniu Trybu gry przywróć poprzedni "
                    + "priorytet; po wyjściu procesu nie jest to potrzebne."));
        }
        else if (plan.BackgroundApplications.Length == 0)
        {
            items.Add(
                new(
                    "NO_SYSTEM_MUTATIONS",
                    "Nie zmieniaj procesów tła ani priorytetu gry, ponieważ "
                    + "ten plan nie zawiera zatwierdzonych optymalizacji.",
                    "Brak",
                    "Nie ma zmian wymagających przywracania."));
        }

        items.Add(
            new(
                "VERIFY_AND_LAUNCH",
                "Ponownie sprawdź SHA-256, dołącz do jednej zgodnej działającej "
                + "gry albo uruchom zatwierdzony plik EXE bez shella.",
                "Niskie",
                "GameShift nie zamyka automatycznie uruchomionej gry."));
        items.Add(
            new(
                "TRACK_AND_RESTORE",
                "Śledź proces gry, a po jego wyjściu automatycznie przywróć "
                + "zamknięte aplikacje.",
                "Niskie",
                "Każde przywrócenie jest weryfikowane i zapisane w journalu."));

        long estimatedBytes = plan.BackgroundApplications
            .Where(application =>
                application.ActionMode
                    == BackgroundProcessActionMode.CloseAndRestore)
            .Sum(application =>
                application.EstimatedWorkingSetBytes);
        int closeCount = plan.BackgroundApplications.Count(application =>
            application.ActionMode
                == BackgroundProcessActionMode.CloseAndRestore);
        int loweredCount =
            plan.BackgroundApplications.Length - closeCount;
        int ecoQosCount = plan.BackgroundApplications.Count(application =>
            application.ActionMode is
                BackgroundProcessActionMode.LowerPriorityAndEcoQos
                or BackgroundProcessActionMode.RestrainBackground);
        int restrainCount = plan.BackgroundApplications.Count(application =>
            application.ActionMode
                == BackgroundProcessActionMode.RestrainBackground);
        return new(
            plan.PlanId,
            plan.SessionId,
            plan.Profile.ProfileId,
            plan.Profile.DisplayName,
            plan.ExpiresAtUtc,
            items,
            SystemMutationsEnabled:
                plan.BackgroundApplications.Length > 0
                || plan.GamePriority is not null,
             $"Do zamknięcia: {closeCount}; do ograniczenia: "
                 + $"{loweredCount}; EcoQoS: {ecoQosCount}; pełny pakiet "
                 + $"(rdzenie tła, pamięć, dysk): {restrainCount}; "
                 + "working set aplikacji do zamknięcia: "
                 + $"{FormatMemory(estimatedBytes)} (to nie jest miara FPS); "
                 + "priorytet gry: "
                 + $"{DescribePriority(plan.GamePriority)}."
                 + DescribeSavedRules(plan.SavedRuleSummary));
    }

    private static GameSessionSnapshot ToSnapshot(
        ActiveRuntime runtime,
        string message) =>
        new(
            runtime.SessionId,
            runtime.ProfileId,
            runtime.GameDisplayName,
            runtime.State,
            runtime.StartedAtUtc,
            runtime.AppliedActionCount,
            runtime.RestoredActionCount,
            runtime.ConflictCount,
            runtime.ErrorCount,
            message,
            runtime.FrameRate.FramesPerSecond,
            runtime.FrameRate.FrameTimeMilliseconds,
            runtime.FrameRate.Message,
            runtime.FrameRate.ProcessId);

    private static void EnsureProfileStillMatches(
        PendingPlan plan,
        ManualGameProfile currentProfile)
    {
        if (!currentProfile.IsEnabled
            || !StringComparer.OrdinalIgnoreCase.Equals(
                currentProfile.ExecutablePath,
                plan.Profile.ExecutablePath)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                currentProfile.ExecutableSha256,
                plan.Profile.ExecutableSha256)
            || currentProfile.Preset != plan.Profile.Preset
            || !currentProfile.LaunchArguments.SequenceEqual(
                plan.Profile.LaunchArguments,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The game profile changed after the plan was prepared.");
        }
    }

    private static SessionRecoveryMetadata DeserializeMetadata(string json) =>
        JsonSerializer.Deserialize<SessionRecoveryMetadata>(
            json,
            SerializerOptions)
        ?? throw new InvalidDataException(
            "The session recovery metadata was null.");

    private void EnsureShutdownNotReserved()
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        ExpireShutdownReservation(now);
        if (_shutdownReservedUntilUtc is not null)
        {
            throw new InvalidOperationException(
                "Gaming component shutdown is already in progress.");
        }
    }

    private void ExpireShutdownReservation(DateTimeOffset now)
    {
        if (_shutdownReservedUntilUtc is { } reservedUntil &&
            reservedUntil <= now)
        {
            _shutdownReservedUntilUtc = null;
        }
    }

    private void EnsureReady()
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "The session orchestrator has not been initialized.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class PendingPlan(
        Guid planId,
        SessionId sessionId,
        ManualGameProfile profile,
        string executableHash,
        IReadOnlyList<PlannedBackgroundApplication> backgroundApplications,
        ProcessPriorityClass? desiredGamePriority,
        SavedRulePlanSummary? savedRuleSummary,
        OptimizationSessionState state,
        DateTimeOffset expiresAtUtc)
    {
        internal Guid PlanId { get; } = planId;

        internal SessionId SessionId { get; } = sessionId;

        internal ManualGameProfile Profile { get; } = profile;

        internal string ExecutableHash { get; } = executableHash;

        internal PlannedBackgroundApplication[]
            BackgroundApplications
        { get; } =
                backgroundApplications.ToArray();

        internal PlannedGamePriority? GamePriority { get; } =
            desiredGamePriority is null
                ? null
                : new(
                    ActionId.Create(),
                    IdempotencyKey.Create(),
                    desiredGamePriority.Value);

        internal SavedRulePlanSummary? SavedRuleSummary { get; } =
            savedRuleSummary;

        internal OptimizationSessionState State { get; set; } = state;

        internal DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    }

    private sealed class ActiveRuntime(
        Guid planId,
        SessionId sessionId,
        GameProfileId profileId,
        string gameDisplayName,
        DateTimeOffset startedAtUtc,
        ProcessIdentity rootProcess,
        GameProcessTreeSessionTracker processTree,
        OptimizationSessionState state,
        IReadOnlyList<PlannedBackgroundApplication> backgroundApplications,
        GamePriorityRecoveryMetadata? gamePriority,
        int appliedActionCount,
        bool recoveredFromHostCrash,
        bool frameRateTrackingEnabled = true,
        bool systemProfileActive = false,
        IReadOnlyList<PlannedBackgroundApplication>? dynamicRestraints = null,
        ulong backgroundAffinityMask = 0)
    {
        private readonly object _restraintLock = new();
        private readonly List<PlannedBackgroundApplication> _dynamicRestraints =
            [.. dynamicRestraints ?? []];
        private readonly object _proBalanceLock = new();
        private Task? _proBalanceStop;

        /// <summary>
        /// The mask background processes were confined to in this session,
        /// or zero when the machine did not qualify. Carried in the checkpoint
        /// so that recovery reverses what was applied rather than what today's
        /// topology read happens to say.
        /// </summary>
        internal ulong BackgroundAffinityMask { get; } = backgroundAffinityMask;

        /// <summary>
        /// Stops the restraint loop exactly once and hands every later caller
        /// the same stop. Closing the session and disposing the orchestrator
        /// can coincide; whoever comes second must wait for the loop's last
        /// decision to finish rather than treat the loop as already gone.
        /// </summary>
        internal Task StopProBalanceOnceAsync(
            Func<ProBalanceSupervisor, Task> stop)
        {
            lock (_proBalanceLock)
            {
                if (_proBalanceStop is null)
                {
                    ProBalanceSupervisor? supervisor = ProBalance;
                    ProBalance = null;
                    _proBalanceStop = supervisor is null
                        ? Task.CompletedTask
                        : stop(supervisor);
                }

                return _proBalanceStop;
            }
        }

        internal Guid PlanId { get; } = planId;

        internal SessionId SessionId { get; } = sessionId;

        internal GameProfileId ProfileId { get; } = profileId;

        internal string GameDisplayName { get; } = gameDisplayName;

        internal DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

        internal ProcessIdentity RootProcess { get; } = rootProcess;

        internal GameProcessTreeSessionTracker ProcessTree { get; } =
            processTree;

        /// <summary>
        /// Reactive background restraint, running for as long as the session
        /// does. Null when the feature is off for this orchestrator.
        /// </summary>
        internal ProBalanceSupervisor? ProBalance { get; set; }

        /// <summary>
        /// Processes the reactive loop has restrained during this session, in
        /// the same shape as the planned applications so one restore path
        /// reverses both — at session end, or after a crash from the checkpoint.
        /// Guarded by a lock of its own: entries arrive from the loop's thread,
        /// including while the session is being closed under the orchestrator's
        /// gate.
        /// </summary>
        internal PlannedBackgroundApplication[] SnapshotDynamicRestraints()
        {
            lock (_restraintLock)
            {
                return [.. _dynamicRestraints];
            }
        }

        /// <summary>
        /// Adds or replaces the entry for the process; returns what it
        /// replaced, so a failed checkpoint write can put it back.
        /// </summary>
        internal PlannedBackgroundApplication? AddDynamicRestraint(
            PlannedBackgroundApplication restraint)
        {
            lock (_restraintLock)
            {
                PlannedBackgroundApplication? previous =
                    _dynamicRestraints.Find(existing =>
                        existing.Identity.RuntimeKey
                            == restraint.Identity.RuntimeKey);
                _dynamicRestraints.RemoveAll(existing =>
                    existing.Identity.RuntimeKey
                        == restraint.Identity.RuntimeKey);
                _dynamicRestraints.Add(restraint);
                return previous;
            }
        }

        internal bool RemoveDynamicRestraint(ProcessRuntimeKey runtimeKey)
        {
            lock (_restraintLock)
            {
                return _dynamicRestraints.RemoveAll(existing =>
                    existing.Identity.RuntimeKey == runtimeKey) > 0;
            }
        }

        internal void ClearDynamicRestraints()
        {
            lock (_restraintLock)
            {
                _dynamicRestraints.Clear();
            }
        }

        private FrameRateSample _frameRate =
            FrameRateSample.WaitingForGame();
        private readonly SessionFrameRateStatisticsAccumulator
            _frameRateStatistics = new();

        internal FrameRateSample FrameRate
        {
            get => Volatile.Read(ref _frameRate);
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                Volatile.Write(ref _frameRate, value);
                _frameRateStatistics.Add(value);
            }
        }

        internal SessionFrameRateStatistics? FrameRateStatistics =>
            _frameRateStatistics.Snapshot();

        internal PlannedBackgroundApplication[]
            BackgroundApplications
        { get; } =
                backgroundApplications.ToArray();

        internal GamePriorityRecoveryMetadata? GamePriority { get; } =
            gamePriority;

        internal int AppliedActionCount { get; } = appliedActionCount;

        internal int RestoredActionCount { get; set; }

        internal int ConflictCount { get; set; }

        internal int ErrorCount { get; set; }

        internal OptimizationSessionState State { get; set; } = state;

        internal bool RecoveredFromHostCrash { get; } =
            recoveredFromHostCrash;

        internal bool FrameRateTrackingEnabled { get; set; } =
            frameRateTrackingEnabled;

        internal bool SystemProfileActive { get; set; } =
            systemProfileActive;
    }

    /// <summary>
    /// The ledger handed to the reactive restraint loop: every restraint it
    /// applies or releases is mirrored into the session's dynamic list and
    /// written as a checkpoint, so a host crash leaves a record that the next
    /// start can reverse. Never touches the orchestrator's gate.
    /// </summary>
    private sealed class RuntimeRestraintLedger(
        LocalGameSessionOrchestrator owner,
        ActiveRuntime runtime) : IRestraintLedger
    {
        public async ValueTask RecordAsync(
            RestrainedProcessRecord record,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(record);
            PlannedBackgroundApplication? previous =
                runtime.AddDynamicRestraint(ToPlannedApplication(record));
            bool recorded = false;
            try
            {
                await owner.CheckpointRestraintsAsync(runtime, cancellationToken)
                    .ConfigureAwait(false);
                recorded = true;
            }
            finally
            {
                if (!recorded)
                {
                    // Wpis w pamieci bez sladu na dysku bylby widmem: przy
                    // zamknieciu sesji przegladalby potomkow procesu, ktorego
                    // aktuator — po naszej odmowie — wcale nie ograniczyl.
                    // Wracamy do stanu sprzed meldunku; blad idzie dalej.
                    if (previous is null)
                    {
                        _ = runtime.RemoveDynamicRestraint(
                            record.Identity.RuntimeKey);
                    }
                    else
                    {
                        _ = runtime.AddDynamicRestraint(previous);
                    }
                }
            }
        }

        public async ValueTask ForgetAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            if (runtime.RemoveDynamicRestraint(runtimeKey))
            {
                await owner.CheckpointRestraintsAsync(
                        runtime,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed record SessionRecoveryMetadata(
        GameProfileId ProfileId,
        string GameDisplayName,
        DateTimeOffset StartedAtUtc,
        ProcessIdentity? RootProcess,
        IReadOnlyList<ProcessIdentity>? TrackedGameProcesses,
        IReadOnlyList<BackgroundApplicationRecoveryMetadata>?
            BackgroundApplications,
        GamePriorityRecoveryMetadata? GamePriority,
        int AppliedActionCount,
        bool FrameRateTrackingEnabled = true,
        bool SystemProfileActive = false,
        // Ograniczenia nalozone reaktywnie w trakcie sesji. Na koncu
        // i z domyslnym null, zeby starsze punkty kontrolne nadal sie
        // odczytywaly.
        IReadOnlyList<BackgroundApplicationRecoveryMetadata>?
            RestrainedProcesses = null,
        // Maska, do ktorej zamknieto tlo. Odtwarzanie ma cofac to, co
        // nalozono, a nie to, co dzisiejszy odczyt topologii by nalozyl.
        ulong? BackgroundAffinityMask = null);

    private sealed record BackgroundApplicationRecoveryMetadata(
        Guid ActionId,
        Guid IdempotencyKey,
        string DisplayName,
        ProcessIdentity Identity,
        BackgroundProcessActionMode ActionMode,
        Guid? EcoQosActionId,
        Guid? EcoQosIdempotencyKey,
        string? WorkingDirectory,
        string[]? Arguments,
        long EstimatedWorkingSetBytes,
        // Na koncu i z domyslnym null, zeby dzienniki zapisane przed
        // wprowadzeniem pakietu dla aplikacji tla nadal sie odczytywaly.
        Guid? AffinityActionId = null,
        Guid? AffinityIdempotencyKey = null,
        Guid? IoPriorityActionId = null,
        Guid? IoPriorityIdempotencyKey = null,
        Guid? MemoryPriorityActionId = null,
        Guid? MemoryPriorityIdempotencyKey = null,
        // Chwila nalozenia ograniczenia reaktywnego i miekkie zbiory CPU.
        // Na koncu i z wartosciami domyslnymi, zeby wczesniejsze punkty
        // kontrolne nadal sie odczytywaly.
        DateTimeOffset? RestrainedAtUtc = null,
        bool SteeredCpuSets = false);

    /// <summary>What the background bundle actually did at session start.</summary>
    private sealed record BackgroundBundleOutcome(
        int AppliedCount,
        int ConfinedCount);

    private sealed record GamePriorityRecoveryMetadata(
        Guid ActionId,
        Guid IdempotencyKey,
        ProcessIdentity Identity,
        ProcessPriorityClass DesiredPriority);

    private sealed record BackgroundRecoveryTotals(
        int RestoredCount,
        int ConflictCount,
        int ErrorCount);

    private sealed record SavedRulePlanSummary(
        int ConfiguredRuleCount,
        int MatchedRuleCount,
        int MatchedProcessCount,
        int ApprovedProcessCount);
}

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
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        ProBalanceSettings? proBalanceSettings = null)
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
                FrameRateTrackingEnabled: enableFrameRateTracking);
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

                int appliedActionCount =
                    await ApplyBackgroundApplicationsAsync(
                            plan.SessionId,
                            plan.BackgroundApplications,
                            startedAtUtc,
                            CancellationToken.None)
                        .ConfigureAwait(false);
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
                    systemProfileActive: systemProfile.WasApplied);
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
                        application.ActionMode
                            == BackgroundProcessActionMode
                                .LowerPriorityAndEcoQos);
                return ToSnapshot(
                    runtime,
                    (launched.WasAlreadyRunning
                        ? "Dołączono do uruchomionej gry. "
                        : "Gra została uruchomiona. ")
                    + "Zamknięte: "
                    + $"{closedApplicationCount}; ograniczone: "
                    + $"{loweredProcessCount}; EcoQoS: "
                    + $"{ecoQosProcessCount}; Windows potwierdził priorytet: "
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

            SessionRecoveryMetadata metadata =
                CreateRecoveryMetadata(runtime);
            await RecordCheckpointAsync(
                    runtime.SessionId,
                    SessionCheckpoint.GameCloseRequested,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);

            GameProcessTreeObservation observation =
                await runtime.ProcessTree.ObserveAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (observation.NewlyDiscoveredCount > 0)
            {
                await RecordCheckpointAsync(
                        runtime.SessionId,
                        SessionCheckpoint.GameProcessTreeObserved,
                        CreateRecoveryMetadata(runtime),
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
                await RecordCheckpointAsync(
                        runtime.SessionId,
                        SessionCheckpoint.GameForceTerminated,
                        CreateRecoveryMetadata(runtime),
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
                            metadata.SystemProfileActive);
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
                    if (observation.NewlyDiscoveredCount > 0)
                    {
                        await RecordCheckpointAsync(
                                recovered.SessionId,
                                SessionCheckpoint.GameProcessTreeObserved,
                                CreateRecoveryMetadata(recovered),
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
            BackgroundRecoveryTotals applicationRecovery =
                await RestoreBackgroundApplicationsAsync(
                        sessionId,
                        backgroundApplications,
                        metadata.StartedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
            BackgroundRecoveryTotals recovery = MergeRecoveryTotals(
                priorityRecovery,
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
            ProBalanceSupervisor supervisor = new(
                new ProcessInventory(),
                new JournaledProBalanceActuator(
                    _journal,
                    runtime.SessionId,
                    _identityProvider,
                    _timeProvider),
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
    private static async ValueTask StopProBalanceAsync(ActiveRuntime runtime)
    {
        if (runtime.ProBalance is not { } supervisor)
        {
            return;
        }

        runtime.ProBalance = null;
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
    }

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

                    await RecordCheckpointAsync(
                            runtime.SessionId,
                            SessionCheckpoint.GameProcessTreeObserved,
                            CreateRecoveryMetadata(runtime),
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
        BackgroundRecoveryTotals applicationRecovery =
            await RestoreBackgroundApplicationsAsync(
                    runtime.SessionId,
                    runtime.BackgroundApplications,
                    runtime.StartedAtUtc,
                    CancellationToken.None)
                .ConfigureAwait(false);
        BackgroundRecoveryTotals recovery = MergeRecoveryTotals(
            priorityRecovery,
            applicationRecovery);
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
        await StopProBalanceAsync(runtime).ConfigureAwait(false);
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
            runtime.SystemProfileActive);

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

        CpuAffinityDecision decision = CpuAffinityPolicy.Decide(
            SystemCpuTopologyProvider.Read(),
            CpuAffinityRole.Foreground);
        if (!decision.ShouldApply)
        {
            return 0;
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

    private async ValueTask<int> ApplyBackgroundApplicationsAsync(
        SessionId sessionId,
        IReadOnlyList<PlannedBackgroundApplication> applications,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken)
    {
        int appliedCount = 0;
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

            if (application.ActionMode
                != BackgroundProcessActionMode.LowerPriorityAndEcoQos)
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
        }

        return appliedCount;
    }

    private async ValueTask<BackgroundRecoveryTotals>
        RestoreBackgroundApplicationsAsync(
            SessionId sessionId,
            IReadOnlyList<PlannedBackgroundApplication> applications,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken)
    {
        BackgroundRecoveryTotals totals = new(0, 0, 0);

        foreach (PlannedBackgroundApplication application
                     in applications.Reverse())
        {
            if (application.ActionMode
                == BackgroundProcessActionMode.LowerPriorityAndEcoQos)
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

        return totals;
    }

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
            application.EstimatedWorkingSetBytes);

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
            actionMode
                == BackgroundProcessActionMode.LowerPriorityAndEcoQos;
        if (requiresEcoQos
            != (ecoQosActionId is not null
                && ecoQosIdempotencyKey is not null))
        {
            throw new InvalidDataException(
                "Journal zawiera niespójne identyfikatory akcji EcoQoS.");
        }

        return new(
            new ActionId(metadata.ActionId),
            new IdempotencyKey(metadata.IdempotencyKey),
            metadata.DisplayName,
            metadata.Identity,
            actionMode,
            ecoQosActionId,
            ecoQosIdempotencyKey,
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
            metadata.EstimatedWorkingSetBytes);
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

    private static SessionPlanPreview ToPreview(PendingPlan plan)
    {
        List<SessionPlanItem> items = [];
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
                    BackgroundProcessActionMode
                            .LowerPriorityAndEcoQos => new(
                        "LIMIT_BACKGROUND_CPU",
                        $"Na czas gry ustaw „{application.DisplayName}” "
                        + "na BelowNormal i włącz EcoQoS, aby ograniczyć "
                        + "rywalizację tła o CPU.",
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
            application.ActionMode
                == BackgroundProcessActionMode.LowerPriorityAndEcoQos);
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
                 + $"{loweredCount}; EcoQoS: {ecoQosCount}; "
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
        bool systemProfileActive = false)
    {
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
        bool SystemProfileActive = false);

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
        long EstimatedWorkingSetBytes);

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

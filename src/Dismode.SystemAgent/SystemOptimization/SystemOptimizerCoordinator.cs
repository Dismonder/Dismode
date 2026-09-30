using System.Security.Cryptography;
using Dismode.Contracts.SystemOptimization;
using Dismode.Core.SystemOptimization;
using Dismode.Data.Journal;
using Dismode.Data.SystemOptimization;

namespace Dismode.SystemAgent.SystemOptimization;

internal sealed class SystemOptimizerCoordinator :
    ISystemOptimizerRecoveryMaintenance
{
    private static readonly TimeSpan StabilizationDuration =
        TimeSpan.FromSeconds(15);

    private readonly SqliteSystemOptimizerStore _store;
    private readonly IHardwareFingerprintProvider _hardwareProvider;
    private readonly ISystemTweakRuntime _runtime;
    private readonly string _recoveryJournalPath;
    private readonly SystemTweakSelectionValidator _selectionValidator = new(
        SystemTweakCatalog.CreateDefault());

    internal SystemOptimizerCoordinator(
        SqliteSystemOptimizerStore store,
        IHardwareFingerprintProvider hardwareProvider,
        ISystemTweakRuntime runtime,
        string recoveryJournalPath)
    {
        _store = store;
        _hardwareProvider = hardwareProvider;
        _runtime = runtime;
        _recoveryJournalPath = Path.GetFullPath(recoveryJournalPath);
    }

    internal async ValueTask<SystemOptimizerStatus> GetStatusAsync(
        string ownerSid,
        bool isReadOnly,
        CancellationToken cancellationToken)
    {
        bool consent = await _store.HasConsentAsync(
                ownerSid,
                cancellationToken)
            .ConfigureAwait(false);
        ExperimentPlan? experiment = await _store.GetActiveExperimentAsync(
                cancellationToken)
            .ConfigureAwait(false);
        GlobalOptimizationProfile? global =
            await _store.GetActiveGlobalProfileAsync(cancellationToken)
                .ConfigureAwait(false);
        ActiveGameOptimizationSession? activeGame =
            await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        RecoveryStatus recovery = await GetCurrentRecoveryAsync(
                cancellationToken)
            .ConfigureAwait(false);
        bool ownsExperiment = experiment is not null
            && StringComparer.OrdinalIgnoreCase.Equals(
                experiment.OwnerSid,
                ownerSid);
        return new(
            IsServiceReady: true,
            isReadOnly,
            consent,
            ownsExperiment ? experiment?.ExperimentId : null,
            global?.ProfileId,
            ActiveGameProfileId: activeGame?.GameProfileId,
            recovery,
            isReadOnly
                ? "Usługa działa w trybie tylko do odczytu, ponieważ klient nie ma zaufanego podpisu."
                : consent
                    ? "System Optimizer jest gotowy."
                    : "Przed pierwszą zmianą wymagana jest świadoma zgoda.",
            DateTimeOffset.UtcNow,
            ownsExperiment ? experiment : null);
    }

    internal HardwareFingerprint GetHardware() => _hardwareProvider.Capture();

    internal static IReadOnlyList<TweakDefinition> GetCatalog() =>
        SystemTweakCatalog.CreateDefault();

    internal async ValueTask SetConsentAsync(
        string ownerSid,
        bool accepted,
        CancellationToken cancellationToken)
    {
        if (!accepted)
        {
            RecoveryStatus recovery = await GetCurrentRecoveryAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (!recovery.IsJournalClean)
            {
                throw new InvalidOperationException(
                    "Najpierw przywróć aktywne zmiany systemowe.");
            }
        }

        await _store.SetConsentAsync(
                ownerSid,
                accepted,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "Consent",
                targetId: null,
                success: true,
                accepted
                    ? "Użytkownik świadomie włączył System Optimizer."
                    : "Użytkownik wyłączył zgodę na System Optimizer.",
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<ExperimentPlan> PrepareExperimentAsync(
        string ownerSid,
        string gameProfileId,
        string gameExecutableSha256,
        string hardwareFingerprintHash,
        TweakSelection selection,
        CancellationToken cancellationToken)
    {
        await EnsureConsentAsync(ownerSid, cancellationToken)
            .ConfigureAwait(false);
        if (await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "Nie można rozpocząć eksperymentu podczas aktywnego profilu gry.");
        }

        ValidateOpaqueIdentifier(gameProfileId, nameof(gameProfileId));
        ValidateSha256(gameExecutableSha256, nameof(gameExecutableSha256));
        HardwareFingerprint hardware = _hardwareProvider.Capture();
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                hardware.FingerprintHash,
                hardwareFingerprintHash))
        {
            throw new InvalidOperationException(
                "Fingerprint sprzętu zmienił się; odśwież zgodność przed testem.");
        }

        SystemTweakValidationResult validation =
            _selectionValidator.Validate(
                [selection],
                allowDangerous: true);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.Error);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExperimentPlan experiment = new(
            Guid.NewGuid(),
            ownerSid,
            gameProfileId,
            gameExecutableSha256.ToUpperInvariant(),
            hardware.FingerprintHash,
            selection,
            RandomNumberGenerator.GetInt32(2) == 0
                ? BenchmarkVariant.Baseline
                : BenchmarkVariant.Candidate,
            ExperimentState.WaitingForScene,
            RequiredPairs: 1,
            CompletedBaselineCaptures: 0,
            CompletedCandidateCaptures: 0,
            now,
            now);
        await _store.UpsertExperimentAsync(
                experiment,
                cancellationToken)
            .ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "ExperimentPrepared",
                experiment.ExperimentId.ToString("D"),
                success: true,
                $"Przygotowano pojedynczy test {selection.TweakId}; pierwsza kolejność: {experiment.FirstVariant}.",
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return experiment;
    }

    internal async ValueTask<CoordinatorExperimentResult> AdvanceExperimentAsync(
        string ownerSid,
        Guid experimentId,
        Dismode.Contracts.Grpc.ExperimentControlAction action,
        CancellationToken cancellationToken)
    {
        ExperimentPlan experiment = await GetOwnedExperimentAsync(
                ownerSid,
                experimentId,
                cancellationToken)
            .ConfigureAwait(false);
        return action switch
        {
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization =>
                await BeginStabilizationAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture =>
                await BeginCaptureAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            Dismode.Contracts.Grpc.ExperimentControlAction.CancelAndRestore =>
                await CancelAndRestoreAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            Dismode.Contracts.Grpc.ExperimentControlAction.ConfirmPostBoot =>
                await ConfirmPostBootAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            Dismode.Contracts.Grpc.ExperimentControlAction.KeepAsGameProfile =>
                await KeepAsGameProfileAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            Dismode.Contracts.Grpc.ExperimentControlAction.KeepGlobally =>
                await KeepGloballyAsync(experiment, cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "The experiment action is not supported."),
        };
    }

    internal async ValueTask<CoordinatorBenchmarkResult> SubmitCaptureAsync(
        string ownerSid,
        Guid experimentId,
        BenchmarkCapture capture,
        CancellationToken cancellationToken)
    {
        ExperimentPlan experiment = await GetOwnedExperimentAsync(
                ownerSid,
                experimentId,
                cancellationToken)
            .ConfigureAwait(false);
        if (experiment.State != ExperimentState.Capturing)
        {
            throw new InvalidOperationException(
                "Eksperyment nie znajduje się w fazie pomiaru.");
        }

        BenchmarkVariant expected = GetExpectedVariant(experiment);
        if (capture.Variant != expected)
        {
            throw new InvalidOperationException(
                $"Oczekiwano wariantu {expected}, a otrzymano {capture.Variant}.");
        }

        string verifiedStateHash =
            await _runtime.GetVerifiedStateHashAsync(
                    ToRuntimeRequest(experiment),
                    experiment.CompletedCandidateCaptures,
                    cancellationToken)
                .ConfigureAwait(false);
        capture = capture with { ProfileStateHash = verifiedStateHash };

        BenchmarkMetrics metrics = BenchmarkAnalyzer.Analyze(capture);
        await _store.AddBenchmarkCaptureAsync(
                experimentId,
                capture,
                metrics,
                cancellationToken)
            .ConfigureAwait(false);
        if (!metrics.IsValid)
        {
            ExperimentPlan retry = experiment with
            {
                State = ExperimentState.WaitingForScene,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await _store.UpsertExperimentAsync(retry, cancellationToken)
                .ConfigureAwait(false);
            return new(
                retry,
                BenchmarkDecision.Invalid(
                    metrics.InvalidReason
                    ?? "Pomiar nie spełnia wymagań jakości."));
        }

        ExperimentPlan updated = experiment with
        {
            CompletedBaselineCaptures =
                experiment.CompletedBaselineCaptures
                + (capture.Variant == BenchmarkVariant.Baseline ? 1 : 0),
            CompletedCandidateCaptures =
                experiment.CompletedCandidateCaptures
                + (capture.Variant == BenchmarkVariant.Candidate ? 1 : 0),
            State = ExperimentState.WaitingForScene,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        IReadOnlyList<BenchmarkMetrics> baseline =
            await _store.ListBenchmarkMetricsAsync(
                    experimentId,
                    BenchmarkVariant.Baseline,
                    cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<BenchmarkMetrics> candidate =
            await _store.ListBenchmarkMetricsAsync(
                    experimentId,
                    BenchmarkVariant.Candidate,
                    cancellationToken)
                .ConfigureAwait(false);
        BenchmarkDecision decision = baseline.Count == candidate.Count
            ? BenchmarkDecisionEngine.Decide(baseline, candidate)
            : new(
                BenchmarkVerdict.Inconclusive,
                RequiresAdditionalPair: false,
                PrimaryMetricImprovementPercent: 0,
                AverageFramesPerSecondChangePercent: 0,
                BaselineHitchRatePercent: 0,
                CandidateHitchRatePercent: 0,
                CompletedPairs: Math.Min(baseline.Count, candidate.Count),
                "Zarejestrowano pierwszy wariant pary; przejdź do tej samej sceny dla drugiego wariantu.");

        if (baseline.Count == candidate.Count)
        {
            if (capture.Variant == BenchmarkVariant.Candidate)
            {
                SystemTweakRuntimeResult restore = await _runtime.RestoreAsync(
                        ToRuntimeRequest(updated),
                        updated.CompletedCandidateCaptures - 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!restore.Succeeded)
                {
                    return await FailRecoveryAsync(
                            updated,
                            decision,
                            restore,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await SetCleanRecoveryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            if (decision.RequiresAdditionalPair)
            {
                updated = updated with { RequiredPairs = 2 };
            }
            else
            {
                updated = updated with { State = ExperimentState.Completed };
                BenchmarkResult storedResult = CreateStoredResult(
                    updated,
                    decision,
                    baseline,
                    candidate);
                await _store.SaveBenchmarkResultAsync(
                        storedResult,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await _store.UpsertExperimentAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "BenchmarkCapture",
                experimentId.ToString("D"),
                success: true,
                $"{capture.Variant}: {metrics.ValidFrameCount} klatek, 1% low {metrics.OnePercentLowFramesPerSecond:F1} FPS. {decision.Explanation}",
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return new(updated, decision);
    }

    internal async ValueTask SavePerGameProfileAsync(
        PerGameOptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        await EnsureConsentAsync(profile.OwnerSid, cancellationToken)
            .ConfigureAwait(false);
        SystemTweakValidationResult validation = _selectionValidator.Validate(
            profile.Selections,
            allowDangerous: false);
        if (!validation.IsValid
            || validation.Definitions.Any(definition =>
                definition.Risk != SystemTweakRisk.Safe
                || definition.Scope != SystemTweakScope.GameSession
                || definition.RestartRequirement != RestartRequirement.None))
        {
            throw new InvalidOperationException(
                validation.Error
                ?? "Profil gry może zawierać wyłącznie bezpieczne, bezrestartowe akcje sesji.");
        }

        await _store.UpsertPerGameProfileAsync(profile, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<SystemOptimizerOperationResult>
        ActivatePerGameProfileAsync(
            string ownerSid,
            string gameProfileId,
            string gameExecutableHash,
            CancellationToken cancellationToken)
    {
        await EnsureConsentAsync(ownerSid, cancellationToken)
            .ConfigureAwait(false);
        ValidateOpaqueIdentifier(gameProfileId, nameof(gameProfileId));
        ValidateSha256(gameExecutableHash, nameof(gameExecutableHash));
        if (await _store.GetActiveExperimentAsync(cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return new(
                Succeeded: false,
                HasConflict: false,
                "Profil gry jest zablokowany przez aktywny eksperyment A/B.");
        }

        ActiveGameOptimizationSession? existing =
            await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        if (existing is not null)
        {
            EnsureOwner(existing.OwnerSid, ownerSid);
            return StringComparer.Ordinal.Equals(
                    existing.GameProfileId,
                    gameProfileId)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    existing.GameExecutableHash,
                    gameExecutableHash)
                ? new(
                    Succeeded: true,
                    HasConflict: false,
                    "Profil tej gry jest już aktywny.")
                : new(
                    Succeeded: false,
                    HasConflict: false,
                    "Inny profil gry jest już aktywny na tym komputerze.");
        }

        PerGameOptimizationProfile profile =
            await _store.GetPerGameProfileAsync(
                    ownerSid,
                    gameProfileId,
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                "Nie znaleziono zapisanego profilu optymalizacji tej gry.");
        if (!profile.Enabled)
        {
            return new(false, false, "Profil optymalizacji tej gry jest wyłączony.");
        }

        HardwareFingerprint hardware = _hardwareProvider.Capture();
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                profile.HardwareFingerprintHash,
                hardware.FingerprintHash))
        {
            return new(
                false,
                false,
                "Profil pochodzi z innego fingerprintu sprzętu lub sterownika; wykonaj ponowny test A/B.");
        }

        SystemTweakValidationResult validation = _selectionValidator.Validate(
            profile.Selections,
            allowDangerous: false);
        if (!validation.IsValid
            || validation.Definitions.Any(definition =>
                definition.Risk != SystemTweakRisk.Safe
                || definition.Scope != SystemTweakScope.GameSession
                || definition.RestartRequirement != RestartRequirement.None))
        {
            throw new InvalidDataException(
                validation.Error
                ?? "Zapisany profil gry nie spełnia aktualnej polityki bezpieczeństwa.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ActiveGameOptimizationSession activation = new(
            Guid.NewGuid(),
            ownerSid,
            profile.ProfileId,
            gameProfileId,
            gameExecutableHash.ToUpperInvariant(),
            hardware.FingerprintHash,
            profile.Selections,
            AppliedSelectionCount: 0,
            now);
        await _store.SetActiveGameSessionAsync(
                activation,
                cancellationToken)
            .ConfigureAwait(false);

        for (int index = 0; index < activation.Selections.Count; index++)
        {
            TweakSelection selection = activation.Selections[index];
            SystemTweakRuntimeResult applied = await _runtime.ApplyAsync(
                    ToRuntimeRequest(activation, selection),
                    index,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                return await RollBackFailedGameActivationAsync(
                        activation,
                        index,
                        applied.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            activation = activation with
            {
                AppliedSelectionCount = index + 1,
            };
            await _store.SetActiveGameSessionAsync(
                    activation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _store.SetRecoveryStatusAsync(
                new(
                    RecoveryPhase.GameSessionActive,
                    activation.ActivationId,
                    now,
                    IsJournalClean: false,
                    HasConflicts: false,
                    ConflictTargets: [],
                    "Bezpieczny profil gry jest aktywny i zostanie cofnięty po zakończeniu procesu."),
                cancellationToken)
            .ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "GameProfileActivated",
                gameProfileId,
                success: true,
                $"Zastosowano {activation.AppliedSelectionCount} bezpieczne akcje sesji.",
                now,
                cancellationToken)
            .ConfigureAwait(false);
        return new(true, false, "Bezpieczny profil gry został zastosowany.");
    }

    internal async ValueTask<SystemOptimizerOperationResult>
        RestoreActiveGameProfileAsync(
            string ownerSid,
            CancellationToken cancellationToken)
    {
        ActiveGameOptimizationSession? active =
            await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        if (active is null)
        {
            return new(true, false, "Brak aktywnego profilu gry.");
        }

        EnsureOwner(active.OwnerSid, ownerSid);
        for (int index = active.AppliedSelectionCount - 1; index >= 0; index--)
        {
            TweakSelection selection = active.Selections[index];
            SystemTweakRuntimeResult restored = await _runtime.RestoreAsync(
                    ToRuntimeRequest(active, selection),
                    index,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!restored.Succeeded)
            {
                ActiveGameOptimizationSession remaining = active with
                {
                    AppliedSelectionCount = index + 1,
                };
                await _store.SetActiveGameSessionAsync(
                        remaining,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.SetRecoveryStatusAsync(
                        new(
                            RecoveryPhase.Failed,
                            active.ActivationId,
                            DateTimeOffset.UtcNow,
                            IsJournalClean: false,
                            restored.HasConflict,
                            [selection.TweakId],
                            restored.Message),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(false, restored.HasConflict, restored.Message);
            }

            active = active with { AppliedSelectionCount = index };
            await _store.SetActiveGameSessionAsync(
                    active,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _store.ClearActiveGameSessionAsync(
                active.ActivationId,
                cancellationToken)
            .ConfigureAwait(false);
        await SetCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "GameProfileRestored",
                active.GameProfileId,
                success: true,
                "Przywrócono stan sprzed profilu gry.",
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return new(true, false, "Profil gry został bezpiecznie cofnięty.");
    }

    internal async ValueTask SaveGlobalDraftAsync(
        GlobalOptimizationProfile profile,
        CancellationToken cancellationToken)
    {
        await EnsureConsentAsync(profile.OwnerSid, cancellationToken)
            .ConfigureAwait(false);
        if (profile.Enabled)
        {
            throw new InvalidOperationException(
                "Profil globalny można aktywować wyłącznie akcją „Pozostaw globalnie” po wygranym teście A/B.");
        }

        SystemTweakValidationResult validation = _selectionValidator.Validate(
            profile.Selections,
            allowDangerous: true);
        if (!validation.IsValid
            || validation.Definitions.Any(definition =>
                definition.Scope != SystemTweakScope.Global))
        {
            throw new InvalidOperationException(
                validation.Error
                ?? "Profil globalny zawiera akcję ograniczoną do sesji gry.");
        }

        await _store.UpsertGlobalProfileAsync(profile, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<SystemOptimizerOperationResult> RestoreAsync(
        string ownerSid,
        bool restoreExperiment,
        bool restoreGlobal,
        CancellationToken cancellationToken)
    {
        List<string> messages = [];
        bool conflict = false;
        if (restoreExperiment)
        {
            ExperimentPlan? active = await _store.GetActiveExperimentAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (active is not null)
            {
                EnsureOwner(active.OwnerSid, ownerSid);
                int appliedIndex = GetAppliedCandidateIndex(active);
                if (appliedIndex >= 0)
                {
                    SystemTweakRuntimeResult restored =
                        await _runtime.RestoreAsync(
                                ToRuntimeRequest(active),
                                appliedIndex,
                                cancellationToken)
                            .ConfigureAwait(false);
                    messages.Add(restored.Message);
                    conflict |= restored.HasConflict;
                    if (!restored.Succeeded)
                    {
                        await SetConflictRecoveryAsync(
                                active,
                                restored.Message,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return new(false, conflict, string.Join(' ', messages));
                    }
                }

                await _store.UpsertExperimentAsync(
                        active with
                        {
                            State = ExperimentState.Failed,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (restoreGlobal)
        {
            GlobalOptimizationProfile? global =
                await _store.GetActiveGlobalProfileAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (global is not null)
            {
                EnsureOwner(global.OwnerSid, ownerSid);
                if (global.SourceExperimentId is not Guid experimentId)
                {
                    return new(
                        Succeeded: false,
                        HasConflict: true,
                        "Aktywny profil globalny nie zawiera źródłowego eksperymentu; zachowano stan do decyzji.");
                }

                ExperimentPlan experiment =
                    await _store.GetExperimentAsync(
                            experimentId,
                            cancellationToken)
                        .ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        "Brakuje źródłowego eksperymentu profilu globalnego.");
                SystemTweakRuntimeResult restored =
                    await _runtime.RestoreAsync(
                            ToRuntimeRequest(experiment),
                            experiment.CompletedCandidateCaptures,
                            cancellationToken)
                        .ConfigureAwait(false);
                messages.Add(restored.Message);
                conflict |= restored.HasConflict;
                if (!restored.Succeeded)
                {
                    await SetConflictRecoveryAsync(
                            experiment,
                            restored.Message,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return new(false, conflict, string.Join(' ', messages));
                }

                await _store.UpsertGlobalProfileAsync(
                        global with
                        {
                            Enabled = false,
                            UpdatedAtUtc = DateTimeOffset.UtcNow,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await SetCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                ownerSid,
                "Restore",
                targetId: null,
                success: true,
                messages.Count == 0
                    ? "Nie było aktywnych zmian do przywrócenia."
                    : string.Join(' ', messages),
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            Succeeded: true,
            HasConflict: false,
            messages.Count == 0
                ? "System jest już w stanie bazowym."
                : string.Join(' ', messages));
    }

    internal async ValueTask<SystemOptimizerOperationResult> PrepareForUpdateAsync(
        CancellationToken cancellationToken)
    {
        ExperimentPlan? active = await _store.GetActiveExperimentAsync(
                cancellationToken)
            .ConfigureAwait(false);
        ActiveGameOptimizationSession? activeGame =
            await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        GlobalOptimizationProfile? global =
            await _store.GetActiveGlobalProfileAsync(cancellationToken)
                .ConfigureAwait(false);
        RecoveryStatus recovery = await GetCurrentRecoveryAsync(
                cancellationToken)
            .ConfigureAwait(false);
        bool stableGlobalProfile = global is
        {
            Enabled: true,
            SourceExperimentId: Guid sourceExperimentId,
        }
            && recovery.Phase == RecoveryPhase.PromotedGlobally
            && recovery.ExperimentId == sourceExperimentId
            && !recovery.HasConflicts;
        if (active is not null
            || activeGame is not null
            || recovery.HasConflicts
            || (!recovery.IsJournalClean && !stableGlobalProfile))
        {
            return new(
                Succeeded: false,
                recovery.HasConflicts,
                "Aktualizacja jest zablokowana do zakończenia eksperymentu i recovery.");
        }

        return new(
            Succeeded: true,
            HasConflict: false,
            "Usługa jest gotowa do bezpiecznej aktualizacji; stabilny profil globalny pozostaje zapisany.");
    }

    internal async ValueTask RecoverAfterUnexpectedRestartAsync(
        CancellationToken cancellationToken)
    {
        RecoveryStatus recovery = await GetCurrentRecoveryAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (recovery.IsJournalClean
            || recovery.Phase == RecoveryPhase.CandidatePendingReboot)
        {
            return;
        }

        ActiveGameOptimizationSession? activeGame =
            await _store.GetActiveGameSessionAsync(cancellationToken)
                .ConfigureAwait(false);
        if (activeGame is not null)
        {
            if (await _runtime.IsOperationTargetActiveAsync(
                    activeGame.ActivationId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            _ = await RestoreActiveGameProfileAsync(
                    activeGame.OwnerSid,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ExperimentPlan? active = await _store.GetActiveExperimentAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (active is null)
        {
            return;
        }

        _ = await RestoreAsync(
                active.OwnerSid,
                restoreExperiment: true,
                restoreGlobal: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask RecoverTimedOutRebootExperimentAsync(
        CancellationToken cancellationToken)
    {
        RecoveryStatus recovery = await GetCurrentRecoveryAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (recovery.PhaseEnteredAtUtc is not DateTimeOffset entered
            || SystemOptimizerRecoveryStateMachine.EvaluateTimeout(
                recovery.Phase,
                entered,
                DateTimeOffset.UtcNow) != RecoveryTimeoutDecision.Restore)
        {
            return;
        }

        ExperimentPlan? active = await _store.GetActiveExperimentAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (active is not null)
        {
            _ = await RestoreAsync(
                    active.OwnerSid,
                    restoreExperiment: true,
                    restoreGlobal: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    ValueTask ISystemOptimizerRecoveryMaintenance
        .RecoverAfterUnexpectedRestartAsync(
            CancellationToken cancellationToken) =>
        RecoverAfterUnexpectedRestartAsync(cancellationToken);

    ValueTask ISystemOptimizerRecoveryMaintenance
        .RecoverTimedOutRebootExperimentAsync(
            CancellationToken cancellationToken) =>
        RecoverTimedOutRebootExperimentAsync(cancellationToken);

    private async ValueTask<CoordinatorExperimentResult> BeginStabilizationAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        if (experiment.State != ExperimentState.WaitingForScene)
        {
            throw new InvalidOperationException(
                "Stabilizację można rozpocząć tylko po wejściu do powtarzalnej sceny.");
        }

        BenchmarkVariant next = GetExpectedVariant(experiment);
        if (next == BenchmarkVariant.Candidate)
        {
            SystemTweakRuntimeResult applied =
                await _runtime.ApplyAsync(
                        ToRuntimeRequest(experiment),
                        experiment.CompletedCandidateCaptures,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!applied.Succeeded)
            {
                throw new InvalidOperationException(applied.Message);
            }

            await _store.SetRecoveryStatusAsync(
                    new(
                        RecoveryPhase.Capture,
                        experiment.ExperimentId,
                        DateTimeOffset.UtcNow,
                        IsJournalClean: false,
                        HasConflicts: false,
                        ConflictTargets: [],
                        "Kandydat jest aktywny; usługa czuwa nad automatycznym przywróceniem."),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            BenchmarkVariant? previous = GetPreviousVariant(experiment);
            if (previous == BenchmarkVariant.Candidate)
            {
                SystemTweakRuntimeResult restored = await _runtime.RestoreAsync(
                        ToRuntimeRequest(experiment),
                        experiment.CompletedCandidateCaptures - 1,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!restored.Succeeded)
                {
                    throw new InvalidOperationException(restored.Message);
                }

                await SetCleanRecoveryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        ExperimentPlan updated = experiment with
        {
            State = ExperimentState.Stabilizing,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.UpsertExperimentAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        return new(
            updated,
            $"Stabilizacja {next}: {StabilizationDuration.TotalSeconds:F0} s. Pomiar nie rozpocznie się automatycznie.");
    }

    private async ValueTask<CoordinatorExperimentResult> BeginCaptureAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        if (experiment.State != ExperimentState.Stabilizing)
        {
            throw new InvalidOperationException(
                "Pomiar można rozpocząć dopiero po świadomie rozpoczętej stabilizacji.");
        }

        ExperimentPlan updated = experiment with
        {
            State = ExperimentState.Capturing,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.UpsertExperimentAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        return new(updated, "Rozpoczęto 90-sekundowe okno pomiarowe.");
    }

    private async ValueTask<CoordinatorExperimentResult> CancelAndRestoreAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        int appliedIndex = GetAppliedCandidateIndex(experiment);
        if (appliedIndex >= 0)
        {
            SystemTweakRuntimeResult restored = await _runtime.RestoreAsync(
                    ToRuntimeRequest(experiment),
                    appliedIndex,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!restored.Succeeded)
            {
                throw new InvalidOperationException(restored.Message);
            }
        }

        ExperimentPlan updated = experiment with
        {
            State = ExperimentState.Failed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.UpsertExperimentAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        await SetCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        return new(updated, "Eksperyment anulowano, a stan został przywrócony.");
    }

    private async ValueTask<CoordinatorExperimentResult> ConfirmPostBootAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        if (experiment.State != ExperimentState.WaitingForRestart)
        {
            throw new InvalidOperationException(
                "Eksperyment nie oczekuje na potwierdzenie po restarcie.");
        }

        ExperimentPlan updated = experiment with
        {
            State = ExperimentState.WaitingForScene,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.UpsertExperimentAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        return new(updated, "Potwierdzono start systemu; przejdź do tej samej sceny gry.");
    }

    private async ValueTask<CoordinatorExperimentResult> KeepAsGameProfileAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        BenchmarkResult result = await RequireWinningResultAsync(
                experiment,
                cancellationToken)
            .ConfigureAwait(false);
        TweakDefinition definition = SystemTweakCatalog.CreateDefault()
            .Single(item => item.Id == experiment.Selection.TweakId);
        if (definition.Risk != SystemTweakRisk.Safe
            || definition.Scope != SystemTweakScope.GameSession
            || definition.RestartRequirement != RestartRequirement.None)
        {
            throw new InvalidOperationException(
                "Tylko bezpieczny, bezrestartowy zwycięzca może zostać profilem gry.");
        }

        PerGameOptimizationProfile profile = new(
            Guid.NewGuid(),
            experiment.OwnerSid,
            experiment.GameProfileId,
            experiment.HardwareFingerprintHash,
            [experiment.Selection],
            Enabled: true,
            DateTimeOffset.UtcNow);
        await _store.UpsertPerGameProfileAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        return new(
            experiment,
            $"Zapisano profil gry po wygranej {result.PrimaryMetricImprovementPercent:F1}%.");
    }

    private async ValueTask<CoordinatorExperimentResult> KeepGloballyAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        _ = await RequireWinningResultAsync(experiment, cancellationToken)
            .ConfigureAwait(false);
        TweakDefinition definition = SystemTweakCatalog.CreateDefault()
            .Single(item => item.Id == experiment.Selection.TweakId);
        if (definition.Scope != SystemTweakScope.Global)
        {
            throw new InvalidOperationException(
                "Ta pozycja może działać wyłącznie podczas sesji gry.");
        }

        SystemTweakRuntimeResult applied = await _runtime.ApplyAsync(
                ToRuntimeRequest(experiment),
                experiment.CompletedCandidateCaptures,
                cancellationToken)
            .ConfigureAwait(false);
        if (!applied.Succeeded)
        {
            throw new InvalidOperationException(applied.Message);
        }

        GlobalOptimizationProfile profile = new(
            Guid.NewGuid(),
            experiment.OwnerSid,
            experiment.HardwareFingerprintHash,
            [experiment.Selection],
            Enabled: true,
            DateTimeOffset.UtcNow,
            experiment.ExperimentId);
        await _store.UpsertGlobalProfileAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        await _store.SetRecoveryStatusAsync(
                new(
                    RecoveryPhase.PromotedGlobally,
                    experiment.ExperimentId,
                    DateTimeOffset.UtcNow,
                    IsJournalClean: false,
                    HasConflicts: false,
                    ConflictTargets: [],
                    "Zwycięski kandydat pozostaje aktywny jako jawny profil globalny."),
                cancellationToken)
            .ConfigureAwait(false);
        return new(experiment, "Kandydat pozostawiono globalnie; restore pozostaje dostępny.");
    }

    private async ValueTask<BenchmarkResult> RequireWinningResultAsync(
        ExperimentPlan experiment,
        CancellationToken cancellationToken)
    {
        if (experiment.State != ExperimentState.Completed)
        {
            throw new InvalidOperationException(
                "Profil można zapisać dopiero po ukończeniu eksperymentu.");
        }

        BenchmarkResult? result = await _store.GetBenchmarkResultAsync(
                experiment.ExperimentId,
                cancellationToken)
            .ConfigureAwait(false);
        return result?.Verdict == BenchmarkVerdict.CandidateWins
            ? result
            : throw new InvalidOperationException(
                "Kandydat nie wygrał pełnej bramy A/B; pozostaje baseline.");
    }

    private async ValueTask<CoordinatorBenchmarkResult> FailRecoveryAsync(
        ExperimentPlan experiment,
        BenchmarkDecision decision,
        SystemTweakRuntimeResult restore,
        CancellationToken cancellationToken)
    {
        ExperimentPlan failed = experiment with
        {
            State = ExperimentState.Failed,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await _store.UpsertExperimentAsync(failed, cancellationToken)
            .ConfigureAwait(false);
        await SetConflictRecoveryAsync(
                experiment,
                restore.Message,
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            failed,
            decision with
            {
                Verdict = BenchmarkVerdict.Invalid,
                RequiresAdditionalPair = false,
                Explanation = restore.Message,
            });
    }

    private async ValueTask<SystemOptimizerOperationResult>
        RollBackFailedGameActivationAsync(
            ActiveGameOptimizationSession activation,
            int failedIndex,
            string failureMessage,
            CancellationToken cancellationToken)
    {
        for (int index = failedIndex - 1; index >= 0; index--)
        {
            TweakSelection selection = activation.Selections[index];
            SystemTweakRuntimeResult restored = await _runtime.RestoreAsync(
                    ToRuntimeRequest(activation, selection),
                    index,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!restored.Succeeded)
            {
                ActiveGameOptimizationSession remaining = activation with
                {
                    AppliedSelectionCount = index + 1,
                };
                await _store.SetActiveGameSessionAsync(
                        remaining,
                        cancellationToken)
                    .ConfigureAwait(false);
                await _store.SetRecoveryStatusAsync(
                        new(
                            RecoveryPhase.Failed,
                            activation.ActivationId,
                            DateTimeOffset.UtcNow,
                            IsJournalClean: false,
                            restored.HasConflict,
                            [selection.TweakId],
                            restored.Message),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new(false, restored.HasConflict, restored.Message);
            }
        }

        await _store.ClearActiveGameSessionAsync(
                activation.ActivationId,
                cancellationToken)
            .ConfigureAwait(false);
        await SetCleanRecoveryAsync(cancellationToken).ConfigureAwait(false);
        _ = await _store.AppendHistoryAsync(
                activation.OwnerSid,
                "GameProfileActivationFailed",
                activation.GameProfileId,
                success: false,
                failureMessage,
                DateTimeOffset.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return new(false, false, failureMessage);
    }

    private async ValueTask<ExperimentPlan> GetOwnedExperimentAsync(
        string ownerSid,
        Guid experimentId,
        CancellationToken cancellationToken)
    {
        ExperimentPlan experiment = await _store.GetExperimentAsync(
                experimentId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Nie znaleziono eksperymentu.");
        EnsureOwner(experiment.OwnerSid, ownerSid);
        return experiment;
    }

    private async ValueTask EnsureConsentAsync(
        string ownerSid,
        CancellationToken cancellationToken)
    {
        if (!await _store.HasConsentAsync(ownerSid, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "System Optimizer pozostaje nieaktywny do pierwszej świadomej zgody.");
        }
    }

    private async ValueTask<RecoveryStatus> GetCurrentRecoveryAsync(
        CancellationToken cancellationToken)
    {
        RecoveryStatus stored = await _store.GetRecoveryStatusAsync(
                cancellationToken)
            .ConfigureAwait(false);
        RecoveryJournalInspection journal =
            await RecoveryJournalInspector.InspectAsync(
                    _recoveryJournalPath,
                    cancellationToken)
                .ConfigureAwait(false);
        return stored with
        {
            IsJournalClean = journal.IsClean && stored.IsJournalClean,
        };
    }

    private ValueTask SetCleanRecoveryAsync(
        CancellationToken cancellationToken) =>
        _store.SetRecoveryStatusAsync(
            new(
                RecoveryPhase.Baseline,
                ExperimentId: null,
                PhaseEnteredAtUtc: null,
                IsJournalClean: true,
                HasConflicts: false,
                ConflictTargets: [],
                "Stan systemu odpowiada baseline."),
            cancellationToken);

    private ValueTask SetConflictRecoveryAsync(
        ExperimentPlan experiment,
        string message,
        CancellationToken cancellationToken) =>
        _store.SetRecoveryStatusAsync(
            new(
                RecoveryPhase.Failed,
                experiment.ExperimentId,
                DateTimeOffset.UtcNow,
                IsJournalClean: false,
                HasConflicts: true,
                ConflictTargets: [experiment.Selection.TweakId],
                message),
            cancellationToken);

    private static BenchmarkVariant GetExpectedVariant(
        ExperimentPlan experiment)
    {
        int captureIndex =
            experiment.CompletedBaselineCaptures
            + experiment.CompletedCandidateCaptures;
        return GetVariantAt(experiment.FirstVariant, captureIndex);
    }

    private static BenchmarkVariant? GetPreviousVariant(
        ExperimentPlan experiment)
    {
        int captures =
            experiment.CompletedBaselineCaptures
            + experiment.CompletedCandidateCaptures;
        return captures == 0
            ? null
            : GetVariantAt(experiment.FirstVariant, captures - 1);
    }

    private static BenchmarkVariant GetVariantAt(
        BenchmarkVariant first,
        int captureIndex) =>
        captureIndex % 2 == 0
            ? first
            : first == BenchmarkVariant.Baseline
                ? BenchmarkVariant.Candidate
                : BenchmarkVariant.Baseline;

    private static int GetAppliedCandidateIndex(ExperimentPlan experiment)
    {
        if (experiment.State is
                ExperimentState.Stabilizing
                or ExperimentState.Capturing
            && GetExpectedVariant(experiment) == BenchmarkVariant.Candidate)
        {
            return experiment.CompletedCandidateCaptures;
        }

        return GetPreviousVariant(experiment) == BenchmarkVariant.Candidate
            ? experiment.CompletedCandidateCaptures - 1
            : -1;
    }

    private static SystemTweakRuntimeRequest ToRuntimeRequest(
        ExperimentPlan experiment) =>
        new(
            experiment.ExperimentId,
            experiment.OwnerSid,
            experiment.GameExecutableHash,
            experiment.Selection);

    private static SystemTweakRuntimeRequest ToRuntimeRequest(
        ActiveGameOptimizationSession activation,
        TweakSelection selection) =>
        new(
            activation.ActivationId,
            activation.OwnerSid,
            activation.GameExecutableHash,
            selection);

    private static BenchmarkResult CreateStoredResult(
        ExperimentPlan experiment,
        BenchmarkDecision decision,
        IReadOnlyList<BenchmarkMetrics> baseline,
        IReadOnlyList<BenchmarkMetrics> candidate) =>
        new(
            experiment.ExperimentId,
            decision.Verdict,
            decision.PrimaryMetricImprovementPercent,
            baseline.Average(item => item.AverageFramesPerSecond),
            candidate.Average(item => item.AverageFramesPerSecond),
            baseline.Average(item => item.OnePercentLowFramesPerSecond),
            candidate.Average(item => item.OnePercentLowFramesPerSecond),
            decision.BaselineHitchRatePercent,
            decision.CandidateHitchRatePercent,
            decision.CompletedPairs,
            decision.Explanation,
            DateTimeOffset.UtcNow);

    private static void EnsureOwner(string actualOwnerSid, string callerSid)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(
                actualOwnerSid,
                callerSid))
        {
            throw new UnauthorizedAccessException(
                "Zasób System Optimizer należy do innego SID.");
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException(
                "Wymagany jest 64-znakowy SHA-256.",
                parameterName);
        }
    }

    private static void ValidateOpaqueIdentifier(
        string value,
        string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException(
                "Identyfikator musi być ograniczonym tokenem serwerowym.",
                parameterName);
        }
    }
}

internal sealed record CoordinatorExperimentResult(
    ExperimentPlan Experiment,
    string Message);

internal sealed record CoordinatorBenchmarkResult(
    ExperimentPlan Experiment,
    BenchmarkDecision Decision);

internal sealed record SystemOptimizerOperationResult(
    bool Succeeded,
    bool HasConflict,
    string Message);

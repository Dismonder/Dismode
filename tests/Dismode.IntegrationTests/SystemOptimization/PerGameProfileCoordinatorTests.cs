using Dismode.Contracts.SystemOptimization;
using Dismode.Core.SystemOptimization;
using Dismode.Data.SystemOptimization;
using Dismode.SystemAgent.SystemOptimization;

namespace Dismode.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class PerGameProfileCoordinatorTests
{
    [TestMethod]
    public async Task IdleRecoveryWaitWakesWhenAPendingPhaseIsObservedAndCancelsAfterCleanup()
    {
        using CoordinatorContext context = new();
        _ = await context.Coordinator.GetStatusAsync(OwnerSid, isReadOnly: false, CancellationToken.None);
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(5));
        Task waiting = context.Coordinator.WaitForPendingTimeoutAsync(
            TimeSpan.FromMinutes(30), lifetime.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);

        await context.Store.SetRecoveryStatusAsync(new(
            RecoveryPhase.Capture, Guid.NewGuid(), DateTimeOffset.UtcNow,
            IsJournalClean: false, HasConflicts: false, ConflictTargets: [], "fixture"), lifetime.Token);
        _ = await context.Coordinator.GetStatusAsync(OwnerSid, isReadOnly: false, lifetime.Token);
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        await context.Coordinator.WaitForPendingTimeoutAsync(
            TimeSpan.FromMinutes(30), lifetime.Token);

        await context.Store.SetRecoveryStatusAsync(new(
            RecoveryPhase.Baseline, null, null,
            IsJournalClean: true, HasConflicts: false, ConflictTargets: [], "fixture"), lifetime.Token);
        _ = await context.Coordinator.GetStatusAsync(OwnerSid, isReadOnly: false, lifetime.Token);
        waiting = context.Coordinator.WaitForPendingTimeoutAsync(
            TimeSpan.FromMinutes(30), lifetime.Token).AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        lifetime.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await waiting);
    }

    private const string OwnerSid = "S-1-5-21-1000";
    private static readonly string ExecutableHash = new('A', 64);
    private static readonly string HardwareHash = new('B', 64);

    [TestMethod]
    public async Task ActivateAndRestorePerGameProfileUsesServerStoredSelections()
    {
        using CoordinatorContext context = new();
        await context.SeedProfileAsync();

        SystemOptimizerOperationResult activated =
            await context.Coordinator.ActivatePerGameProfileAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                CancellationToken.None);
        ActiveGameOptimizationSession? active =
            await context.Store.GetActiveGameSessionAsync(
                CancellationToken.None);

        Assert.IsTrue(activated.Succeeded);
        Assert.IsNotNull(active);
        Assert.AreEqual("game-a", active.GameProfileId);
        Assert.HasCount(2, context.Runtime.Applied);
        Assert.AreEqual(
            "process.game.priority",
            context.Runtime.Applied[0].Request.Selection.TweakId);
        Assert.AreEqual(
            "process.game.power-throttling",
            context.Runtime.Applied[1].Request.Selection.TweakId);

        SystemOptimizerOperationResult restored =
            await context.Coordinator.RestoreActiveGameProfileAsync(
                OwnerSid,
                CancellationToken.None);

        Assert.IsTrue(restored.Succeeded);
        Assert.IsNull(await context.Store.GetActiveGameSessionAsync(
            CancellationToken.None));
        Assert.HasCount(2, context.Runtime.Restored);
        Assert.AreEqual(1, context.Runtime.Restored[0].Index);
        Assert.AreEqual(0, context.Runtime.Restored[1].Index);
    }

    [TestMethod]
    public async Task FailedActivationRollsBackAlreadyAppliedSelection()
    {
        using CoordinatorContext context = new();
        await context.SeedProfileAsync();
        context.Runtime.FailApplyAtIndex = 1;

        SystemOptimizerOperationResult result =
            await context.Coordinator.ActivatePerGameProfileAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.HasCount(1, context.Runtime.Restored);
        Assert.AreEqual(0, context.Runtime.Restored[0].Index);
        Assert.IsNull(await context.Store.GetActiveGameSessionAsync(
            CancellationToken.None));
    }

    [TestMethod]
    public async Task StartupRecoveryRestoresActivePerGameProfile()
    {
        using CoordinatorContext context = new();
        await context.SeedProfileAsync();
        _ = await context.Coordinator.ActivatePerGameProfileAsync(
            OwnerSid,
            "game-a",
            ExecutableHash,
            CancellationToken.None);
        context.Runtime.Restored.Clear();

        await context.Coordinator.RecoverAfterUnexpectedRestartAsync(
            CancellationToken.None);

        Assert.HasCount(2, context.Runtime.Restored);
        Assert.IsNull(await context.Store.GetActiveGameSessionAsync(
            CancellationToken.None));
    }

    [TestMethod]
    public async Task StartupRecoveryKeepsProfileWhileOriginalGameProcessRuns()
    {
        using CoordinatorContext context = new();
        await context.SeedProfileAsync();
        _ = await context.Coordinator.ActivatePerGameProfileAsync(
            OwnerSid,
            "game-a",
            ExecutableHash,
            CancellationToken.None);
        context.Runtime.Restored.Clear();
        context.Runtime.IsOperationTargetActive = true;

        await context.Coordinator.RecoverAfterUnexpectedRestartAsync(
            CancellationToken.None);

        Assert.IsEmpty(context.Runtime.Restored);
        Assert.IsNotNull(await context.Store.GetActiveGameSessionAsync(
            CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelledActivationStillRecordsAppliedSelection()
    {
        using CoordinatorContext context = new();
        await context.SeedProfileAsync();
        using CancellationTokenSource cancellation = new();
        context.Runtime.CancelAfterApply = cancellation;

        _ = await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await context.Coordinator.ActivatePerGameProfileAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                cancellation.Token));
        ActiveGameOptimizationSession? active =
            await context.Store.GetActiveGameSessionAsync(
                CancellationToken.None);

        Assert.IsNotNull(active);
        Assert.AreEqual(1, active.AppliedSelectionCount);
        SystemOptimizerOperationResult restored =
            await context.Coordinator.RestoreActiveGameProfileAsync(
                OwnerSid,
                CancellationToken.None);
        Assert.IsTrue(restored.Succeeded);
        Assert.HasCount(1, context.Runtime.Restored);
        Assert.AreEqual(0, context.Runtime.Restored[0].Index);
    }

    [TestMethod]
    public async Task LossCannotCompleteExperimentAndACleanRetryUsesOnlyValidCaptures()
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(OwnerSid, true, DateTimeOffset.UtcNow, CancellationToken.None);
        ExperimentPlan experiment = await context.Coordinator.PrepareExperimentAsync(OwnerSid,
            "game-a", ExecutableHash, HardwareHash, new("process.game.priority", 1, "above-normal"), CancellationToken.None);
        await AdvanceAsync(context, experiment, Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization);
        await AdvanceAsync(context, experiment, Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture);
        CoordinatorBenchmarkResult invalid = await context.Coordinator.SubmitCaptureAsync(OwnerSid,
            experiment.ExperimentId, CreateCapture(experiment.FirstVariant, Enumerable.Repeat(15d, 6_000).ToArray()) with
            {
                CaptureLoss = new(BenchmarkCaptureLossState.Confirmed, LostEvents: 24000),
            }, CancellationToken.None);
        Assert.AreEqual(BenchmarkVerdict.Invalid, invalid.Decision.Verdict);
        StringAssert.Contains(invalid.Decision.Explanation, BenchmarkAnalyzer.CaptureLossReason);
        Assert.AreEqual(ExperimentState.WaitingForScene, invalid.Experiment.State);
        Assert.AreEqual(0, invalid.Experiment.CompletedBaselineCaptures);
        Assert.AreEqual(0, invalid.Experiment.CompletedCandidateCaptures);
        BenchmarkVariant other = experiment.FirstVariant == BenchmarkVariant.Baseline
            ? BenchmarkVariant.Candidate : BenchmarkVariant.Baseline;
        CoordinatorBenchmarkResult result = invalid;
        foreach (BenchmarkVariant variant in new[] { experiment.FirstVariant, other })
        {
            await AdvanceAsync(context, experiment, Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization);
            await AdvanceAsync(context, experiment, Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture);
            result = await context.Coordinator.SubmitCaptureAsync(OwnerSid, experiment.ExperimentId,
                CreateCapture(variant, Enumerable.Repeat(variant == BenchmarkVariant.Baseline ? 16.67d : 15d, 6_000).ToArray()),
                CancellationToken.None);
        }

        Assert.AreEqual(BenchmarkVerdict.CandidateWins, result.Decision.Verdict);
        Assert.AreEqual(ExperimentState.Completed, result.Experiment.State);
        IReadOnlyList<BenchmarkMetrics> history = await context.Store.ListBenchmarkMetricsAsync(
            experiment.ExperimentId, experiment.FirstVariant, CancellationToken.None);
        Assert.HasCount(2, history);
        Assert.AreEqual(1, history.Count(metrics => !metrics.IsValid));
    }

    [TestMethod]
    public async Task CancelAfterInvalidCandidateCaptureRestoresCandidate()
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        ExperimentPlan experiment =
            await context.Coordinator.PrepareExperimentAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                HardwareHash,
                new("process.game.priority", 1, "above-normal"),
                CancellationToken.None);
        if (experiment.FirstVariant == BenchmarkVariant.Baseline)
        {
            await AdvanceAsync(
                context,
                experiment,
                Dismode.Contracts.Grpc.ExperimentControlAction
                    .BeginStabilization);
            await AdvanceAsync(
                context,
                experiment,
                Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture);
            CoordinatorBenchmarkResult valid =
                await context.Coordinator.SubmitCaptureAsync(
                    OwnerSid,
                    experiment.ExperimentId,
                    CreateCapture(
                        BenchmarkVariant.Baseline,
                        Enumerable.Repeat(16.67d, 6_000).ToArray()),
                    CancellationToken.None);
            Assert.AreNotEqual(BenchmarkVerdict.Invalid, valid.Decision.Verdict);
        }

        await AdvanceAsync(
            context,
            experiment,
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization);
        await AdvanceAsync(
            context,
            experiment,
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture);
        CoordinatorBenchmarkResult invalid =
            await context.Coordinator.SubmitCaptureAsync(
                OwnerSid,
                experiment.ExperimentId,
                CreateCapture(
                    BenchmarkVariant.Candidate,
                    Enumerable.Repeat(16.67d, 1_200).ToArray()),
                CancellationToken.None);
        Assert.AreEqual(BenchmarkVerdict.Invalid, invalid.Decision.Verdict);
        Assert.AreEqual(
            ExperimentState.WaitingForScene,
            invalid.Experiment.State);
        context.Runtime.Restored.Clear();

        _ = await AdvanceAsync(
            context,
            experiment,
            Dismode.Contracts.Grpc.ExperimentControlAction.CancelAndRestore);

        Assert.AreEqual(1, context.Runtime.Restored.Count);
        Assert.AreEqual(0, context.Runtime.Restored[0].Index);
    }

    [TestMethod]
    public async Task CancelBeforeCandidateWasAppliedDoesNotRestore()
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        ExperimentPlan experiment =
            await context.Coordinator.PrepareExperimentAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                HardwareHash,
                new("process.game.priority", 1, "above-normal"),
                CancellationToken.None);
        if (experiment.FirstVariant == BenchmarkVariant.Baseline)
        {
            await AdvanceAsync(
                context,
                experiment,
                Dismode.Contracts.Grpc.ExperimentControlAction
                    .BeginStabilization);
            await AdvanceAsync(
                context,
                experiment,
                Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture);
            _ = await context.Coordinator.SubmitCaptureAsync(
                OwnerSid,
                experiment.ExperimentId,
                CreateCapture(
                    BenchmarkVariant.Baseline,
                    Enumerable.Repeat(16.67d, 6_000).ToArray()),
                CancellationToken.None);
        }

        CoordinatorExperimentResult cancelled = await AdvanceAsync(
            context,
            experiment,
            Dismode.Contracts.Grpc.ExperimentControlAction.CancelAndRestore);

        Assert.AreEqual(ExperimentState.Failed, cancelled.Experiment.State);
        Assert.AreEqual(0, context.Runtime.Restored.Count);
    }

    [TestMethod]
    public async Task BenchmarkCaptureUsesServerVerifiedStateHash()
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        ExperimentPlan experiment =
            await context.Coordinator.PrepareExperimentAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                HardwareHash,
                new("process.game.priority", 1, "above-normal"),
                CancellationToken.None);
        _ = await context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization,
            CancellationToken.None);
        _ = await context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture,
            CancellationToken.None);

        CoordinatorBenchmarkResult result =
            await context.Coordinator.SubmitCaptureAsync(
                OwnerSid,
                experiment.ExperimentId,
                new(
                    Guid.NewGuid(),
                    experiment.FirstVariant,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromSeconds(15),
                    TimeSpan.FromSeconds(90),
                    Enumerable.Repeat(16.67d, 6_000).ToArray(),
                    AverageCpuPercent: 20,
                    AverageGpuBusyPercent: 80,
                    AverageUsedRamBytes: 8L * 1024 * 1024 * 1024,
                    ProfileStateHash: string.Empty),
                CancellationToken.None);

        Assert.AreNotEqual(BenchmarkVerdict.Invalid, result.Decision.Verdict);
        Assert.AreEqual(1, context.Runtime.StateHashRequests);
    }

    [TestMethod]
    public async Task PrepareForUpdateAllowsVerifiedStableGlobalProfile()
    {
        using CoordinatorContext context = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExperimentPlan experiment = new(
            Guid.NewGuid(),
            OwnerSid,
            "game-a",
            ExecutableHash,
            HardwareHash,
            new(
                "power.hibernate",
                1,
                "enabled",
                DangerousConfirmation: "ROZUMIEM RYZYKO"),
            BenchmarkVariant.Baseline,
            ExperimentState.Completed,
            RequiredPairs: 1,
            CompletedBaselineCaptures: 1,
            CompletedCandidateCaptures: 1,
            now,
            now);
        await context.Store.UpsertExperimentAsync(
            experiment,
            CancellationToken.None);
        await context.Store.UpsertGlobalProfileAsync(
            new(
                Guid.NewGuid(),
                OwnerSid,
                HardwareHash,
                [experiment.Selection],
                Enabled: true,
                now,
                experiment.ExperimentId),
            CancellationToken.None);
        await context.Store.SetRecoveryStatusAsync(
            new(
                RecoveryPhase.PromotedGlobally,
                experiment.ExperimentId,
                now,
                IsJournalClean: false,
                HasConflicts: false,
                ConflictTargets: [],
                "Stable global profile."),
            CancellationToken.None);

        SystemOptimizerOperationResult result =
            await context.Coordinator.PrepareForUpdateAsync(
                CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.HasConflict);
    }

    [TestMethod]
    public async Task DangerousExperimentRejectsMissingPerItemConfirmation()
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Coordinator.PrepareExperimentAsync(
                    OwnerSid,
                    "game-a",
                    ExecutableHash,
                    HardwareHash,
                    new("power.hibernate", 1, "enabled"),
                    CancellationToken.None)
                .AsTask());
    }

    [TestMethod]
    [DataRow("power.hibernate", "enabled", true, true)]
    [DataRow("gaming.background-capture", "disabled", false, true)]
    [DataRow("gaming.background-capture", "disabled", false, false)]
    public async Task GlobalExperimentRequiresWinningBenchmarkAndExplicitPromotion(
        string tweakId, string value, bool dangerous, bool candidateWins)
    {
        using CoordinatorContext context = new();
        await context.Store.SetConsentAsync(
            OwnerSid,
            accepted: true,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        ExperimentPlan experiment =
            await context.Coordinator.PrepareExperimentAsync(
                OwnerSid,
                "game-a",
                ExecutableHash,
                HardwareHash,
                new(
                    tweakId,
                    1,
                    value,
                    DangerousConfirmation:
                        dangerous ? SystemTweakSelectionValidator.DangerousConfirmationText : null),
                CancellationToken.None);

        BenchmarkDecision? finalDecision = null;
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Coordinator.AdvanceExperimentAsync(
                OwnerSid,
                experiment.ExperimentId,
                Dismode.Contracts.Grpc.ExperimentControlAction.KeepGlobally,
                CancellationToken.None).AsTask());
        int captureCount = candidateWins ? 2 : 4;
        for (int captureIndex = 0; captureIndex < captureCount; captureIndex++)
        {
            BenchmarkVariant variant = captureIndex % 2 == 0
                ? experiment.FirstVariant
                : experiment.FirstVariant == BenchmarkVariant.Baseline
                    ? BenchmarkVariant.Candidate
                    : BenchmarkVariant.Baseline;
            _ = await context.Coordinator.AdvanceExperimentAsync(
                OwnerSid,
                experiment.ExperimentId,
                Dismode.Contracts.Grpc.ExperimentControlAction.BeginStabilization,
                CancellationToken.None);
            _ = await context.Coordinator.AdvanceExperimentAsync(
                OwnerSid,
                experiment.ExperimentId,
                Dismode.Contracts.Grpc.ExperimentControlAction.BeginCapture,
                CancellationToken.None);
            CoordinatorBenchmarkResult capture =
                await context.Coordinator.SubmitCaptureAsync(
                    OwnerSid,
                    experiment.ExperimentId,
                    new(
                        Guid.NewGuid(),
                        variant,
                        DateTimeOffset.UtcNow,
                        TimeSpan.FromSeconds(15),
                        TimeSpan.FromSeconds(90),
                        Enumerable.Repeat(
                                candidateWins && variant == BenchmarkVariant.Candidate
                                    ? 15.5d
                                    : 16.67d,
                                6_000)
                            .ToArray(),
                        AverageCpuPercent: 20,
                        AverageGpuBusyPercent: 80,
                        AverageUsedRamBytes: 8L * 1024 * 1024 * 1024,
                        ProfileStateHash: string.Empty),
                    CancellationToken.None);
            experiment = capture.Experiment;
            finalDecision = capture.Decision;
        }

        Assert.IsNotNull(finalDecision);
        Assert.AreEqual(candidateWins ? BenchmarkVerdict.CandidateWins : BenchmarkVerdict.KeepBaseline, finalDecision.Verdict);
        Assert.IsNull(await context.Store.GetActiveGlobalProfileAsync(
            CancellationToken.None));

        if (!candidateWins)
        {
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Coordinator.AdvanceExperimentAsync(
                    OwnerSid,
                    experiment.ExperimentId,
                    Dismode.Contracts.Grpc.ExperimentControlAction.KeepGlobally,
                    CancellationToken.None).AsTask());
            return;
        }

        _ = await context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            Dismode.Contracts.Grpc.ExperimentControlAction.KeepGlobally,
            CancellationToken.None);

        GlobalOptimizationProfile? global =
            await context.Store.GetActiveGlobalProfileAsync(
                CancellationToken.None);
        RecoveryStatus recovery = await context.Store.GetRecoveryStatusAsync(
            CancellationToken.None);
        Assert.IsNotNull(global);
        Assert.AreEqual(experiment.ExperimentId, global.SourceExperimentId);
        Assert.AreEqual(RecoveryPhase.PromotedGlobally, recovery.Phase);
        Assert.IsFalse(recovery.IsJournalClean);
    }

    private static ValueTask<CoordinatorExperimentResult> AdvanceAsync(
        CoordinatorContext context,
        ExperimentPlan experiment,
        Dismode.Contracts.Grpc.ExperimentControlAction action) =>
        context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            action,
            CancellationToken.None);

    private static BenchmarkCapture CreateCapture(
        BenchmarkVariant variant,
        double[] frameTimes) =>
        new(
            Guid.NewGuid(),
            variant,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(90),
            frameTimes,
            AverageCpuPercent: 20,
            AverageGpuBusyPercent: 80,
            AverageUsedRamBytes: 8L * 1024 * 1024 * 1024,
            ProfileStateHash: string.Empty);

    private sealed class CoordinatorContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode-PerGameCoordinatorTests",
            Guid.NewGuid().ToString("N"));

        internal CoordinatorContext()
        {
            Directory.CreateDirectory(_directory);
            Store = new(Path.Combine(_directory, "system-optimizer.db"));
            Runtime = new();
            Coordinator = new(
                Store,
                new FixedHardwareProvider(),
                Runtime,
                Path.Combine(_directory, "machine-recovery.jsonl"));
        }

        internal SqliteSystemOptimizerStore Store { get; }

        internal FakeSystemTweakRuntime Runtime { get; }

        internal SystemOptimizerCoordinator Coordinator { get; }

        internal async Task SeedProfileAsync()
        {
            await Store.SetConsentAsync(
                OwnerSid,
                accepted: true,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            await Store.UpsertPerGameProfileAsync(
                new(
                    Guid.NewGuid(),
                    OwnerSid,
                    "game-a",
                    HardwareHash,
                    [
                        new("process.game.priority", 1, "above-normal"),
                        new(
                            "process.game.power-throttling",
                            1,
                            "disabled"),
                    ],
                    Enabled: true,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FixedHardwareProvider : IHardwareFingerprintProvider
    {
        public HardwareFingerprint Capture() =>
            new(
                SchemaVersion: 1,
                WindowsBuild: "26100.1",
                CpuArchitecture: "X64",
                CpuVendor: "Test",
                CpuModelFamily: "Test",
                GraphicsAdapters: [],
                GraphicsDriverVersions: [],
                NetworkAdapterClasses: [],
                PhysicalMemoryBytes: 16L * 1024 * 1024 * 1024,
                IsLaptop: false,
                HardwareHash);
    }

    private sealed class FakeSystemTweakRuntime : ISystemTweakRuntime
    {
        internal List<(SystemTweakRuntimeRequest Request, int Index)> Applied
        {
            get;
        } = [];

        internal List<(SystemTweakRuntimeRequest Request, int Index)> Restored
        {
            get;
        } = [];

        internal int? FailApplyAtIndex { get; set; }

        internal CancellationTokenSource? CancelAfterApply { get; set; }

        internal bool IsOperationTargetActive { get; set; }

        internal int StateHashRequests { get; private set; }

        public ValueTask<SystemTweakRuntimeResult> ApplyAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
        {
            if (FailApplyAtIndex == applicationIndex)
            {
                return ValueTask.FromResult(
                    SystemTweakRuntimeResult.Blocked("controlled failure"));
            }

            Applied.Add((request, applicationIndex));
            CancelAfterApply?.Cancel();
            return ValueTask.FromResult(
                SystemTweakRuntimeResult.Success("applied"));
        }

        public ValueTask<SystemTweakRuntimeResult> RestoreAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
        {
            Restored.Add((request, applicationIndex));
            return ValueTask.FromResult(
                SystemTweakRuntimeResult.Success("restored"));
        }

        public ValueTask<bool> IsOperationTargetActiveAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(IsOperationTargetActive);

        public ValueTask<string> GetVerifiedStateHashAsync(
            SystemTweakRuntimeRequest request,
            int applicationIndex,
            CancellationToken cancellationToken)
        {
            StateHashRequests++;
            return ValueTask.FromResult(new string('C', 64));
        }
    }
}

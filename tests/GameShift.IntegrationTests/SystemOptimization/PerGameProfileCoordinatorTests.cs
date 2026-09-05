using GameShift.Contracts.SystemOptimization;
using GameShift.Core.SystemOptimization;
using GameShift.Data.SystemOptimization;
using GameShift.SystemAgent.SystemOptimization;

namespace GameShift.IntegrationTests.SystemOptimization;

[TestClass]
public sealed class PerGameProfileCoordinatorTests
{
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
            GameShift.Contracts.Grpc.ExperimentControlAction.BeginStabilization,
            CancellationToken.None);
        _ = await context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            GameShift.Contracts.Grpc.ExperimentControlAction.BeginCapture,
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
    public async Task WinningDangerousExperimentRequiresExplicitGlobalPromotion()
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
                    "power.hibernate",
                    1,
                    "enabled",
                    DangerousConfirmation:
                        SystemTweakSelectionValidator.DangerousConfirmationText),
                CancellationToken.None);

        BenchmarkDecision? finalDecision = null;
        for (int captureIndex = 0; captureIndex < 2; captureIndex++)
        {
            BenchmarkVariant variant = captureIndex == 0
                ? experiment.FirstVariant
                : experiment.FirstVariant == BenchmarkVariant.Baseline
                    ? BenchmarkVariant.Candidate
                    : BenchmarkVariant.Baseline;
            _ = await context.Coordinator.AdvanceExperimentAsync(
                OwnerSid,
                experiment.ExperimentId,
                GameShift.Contracts.Grpc.ExperimentControlAction.BeginStabilization,
                CancellationToken.None);
            _ = await context.Coordinator.AdvanceExperimentAsync(
                OwnerSid,
                experiment.ExperimentId,
                GameShift.Contracts.Grpc.ExperimentControlAction.BeginCapture,
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
                                variant == BenchmarkVariant.Candidate
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
        Assert.AreEqual(BenchmarkVerdict.CandidateWins, finalDecision.Verdict);
        Assert.IsNull(await context.Store.GetActiveGlobalProfileAsync(
            CancellationToken.None));

        _ = await context.Coordinator.AdvanceExperimentAsync(
            OwnerSid,
            experiment.ExperimentId,
            GameShift.Contracts.Grpc.ExperimentControlAction.KeepGlobally,
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

    private sealed class CoordinatorContext : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            "GameShift-PerGameCoordinatorTests",
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

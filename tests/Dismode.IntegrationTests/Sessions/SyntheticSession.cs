using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Profiles;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.Windows.Cpu;
using Dismode.Windows.Display;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

internal sealed class SyntheticSession : IAsyncDisposable, IProcessIdentityProvider,
    IProcessInventory, IProcessParentMapProvider, ICpuProcessSource
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private bool _started;
    private readonly bool _lateRules;
    private readonly object _inventoryGate = new();
    private readonly string _profilePath;
    private readonly string _profileHash;
    internal SyntheticSession(IDisplayApi? displayApi = null, bool lateRules = false,
        ISystemGameProfileCoordinator? systemProfileCoordinator = null,
        string? profileExecutablePath = null, string? profileExecutableSha256 = null,
        string? rootExecutablePath = null, bool refuseAdoption = false,
        ICpuSetsPort? cpuSetsPort = null,
        Func<IRecoveryJournal, IRecoveryJournal>? journalDecorator = null,
        TimeProvider? timeProvider = null)
    {
        _lateRules = lateRules;
        Directory.CreateDirectory(_directory);
        Store = new(Path.Combine(_directory, "user.db"));
        Journal = new(Path.Combine(_directory, "recovery.jsonl"));
        Root = new(new(12345, Clock.GetUtcNow().AddSeconds(-10)), rootExecutablePath ?? @"C:\Games\Synthetic\game.exe",
            new string('A', 64), null, "user", 1, null);
        _profilePath = profileExecutablePath ?? Root.ExecutablePath;
        _profileHash = profileExecutableSha256 ?? Root.ExecutableSha256;
        Running.Add(Root);
        Orchestrator = new(Store, Store, journalDecorator?.Invoke(Journal) ?? Journal, identityProvider: this,
            timeProvider: timeProvider ?? Clock, monitorInterval: TimeSpan.FromMilliseconds(5),
            processParentMapProvider: this, processInventory: this, gameplayClock: Clock, displayApi: displayApi,
            savedRuleResolver: new(this), systemProfileCoordinator: systemProfileCoordinator,
            cpuProcessSourceFactory: () => this)
        {
            CpuSetsPort = cpuSetsPort ?? SystemCpuSetsPort.Instance,
            ForegroundProcessId = () => ForegroundPid,
            HandoffAdopter = (tree, identity) => !refuseAdoption && tree.Adopt(identity),
            LateRuleError = exception => LateErrors.Enqueue(exception),
            LateRuleApprover = async (selection, _, _) =>
            {
                if (BeforeLateApproval is { } before) { await before(); }
                return new PlannedBackgroundApplication(
                ActionId.Create(), Dismode.Contracts.Protocol.IdempotencyKey.Create(), "SyntheticBackground",
                Running.Single(identity => identity.RuntimeKey.ProcessId == selection.ProcessId),
                selection.ActionMode, null, null, null, null, null, null, null, null, null, 0);
            },
            BackgroundPriorityActionFactory = application => new FakePriorityAction(application, this),
        };
    }

    internal SqliteUserDataStore Store { get; }
    internal AppendOnlyRecoveryJournal Journal { get; }
    internal LocalGameSessionOrchestrator Orchestrator { get; }
    internal ProcessIdentity Root { get; }
    internal ConcurrentBag<ProcessIdentity> Running { get; } = [];
    internal ConcurrentDictionary<ProcessRuntimeKey, ProcessPriorityClass> Priorities { get; } = new();
    internal ConcurrentDictionary<int, int> Parents { get; } = new();
    internal int ForegroundPid { get; set; }
    internal int PrioritySetCount;
    internal int InventoryCaptureCount;
    internal ConcurrentQueue<Exception> LateErrors { get; } = new();
    internal Func<Task>? BeforeLateApproval { get; set; }
    internal ManualClock Clock { get; } = new();
    internal SessionId Id { get; } = SessionId.Create();
    internal GameProfileId ProfileId { get; } = GameProfileId.Create();

    internal async Task StartAsync(object? displayRefresh = null, bool systemProfileActive = false,
        DateTimeOffset? lateRulesResolvedAtUtc = null, bool refreshRatePending = false,
        object? gameCpuSets = null, object[]? restrainedProcesses = null)
    {
        await Store.UpsertAsync(new(ProfileId, "Synthetic", _profilePath, _profileHash,
            @"C:\", [], OptimizationPreset.Safe, true, Clock.GetUtcNow(), Clock.GetUtcNow()), CancellationToken.None);
        await new SessionCheckpointWriter(Journal).RecordAsync(Id, SessionCheckpoint.SessionActivated,
            JsonSerializer.Serialize(new
            {
                ProfileId,
                GameDisplayName = "Synthetic",
                StartedAtUtc = Root.RuntimeKey.StartedAtUtc,
                RootProcess = Root,
                TrackedGameProcesses = new[] { Root },
                BackgroundApplications = Array.Empty<object>(),
                AppliedActionCount = 0,
                FrameRateTrackingEnabled = false,
                DurationSeconds = 0,
                HandoffIdentity = new GameHandoffIdentity(_profilePath, _profileHash,
                    Running.Where(identity => identity.ExecutablePath == Root.ExecutablePath
                        || identity.ExecutablePath == _profilePath).Select(identity => identity.RuntimeKey).ToArray()),
                DisplayRefresh = displayRefresh,
                GameCpuSets = gameCpuSets,
                RestrainedProcesses = restrainedProcesses,
                SystemProfileActive = systemProfileActive,
                RefreshRatePending = refreshRatePending,
                LateRules = !_lateRules ? null : new
                {
                    ActivatedAtUtc = lateRulesResolvedAtUtc ?? Clock.GetUtcNow(),
                    Rules = new[] { new { Path = @"C:\Apps\background.exe", Mode = SavedBackgroundActionMode.LowerPriority } },
                    Attempted = Array.Empty<ProcessRuntimeKey>(),
                },
            }), CancellationToken.None);
        await Orchestrator.InitializeAsync(CancellationToken.None);
        _started = true;
    }

    internal async Task WaitForCheckpointAsync(SessionCheckpoint checkpoint)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!(await Journal.ReadAllAsync(timeout.Token)).Any(entry => entry.SessionCheckpoint == checkpoint))
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    internal void AddCandidates(params ProcessIdentity[] candidates)
    {
        lock (_inventoryGate)
        {
            foreach (ProcessIdentity identity in candidates) { Running.Add(identity); }
        }
    }

    public IReadOnlyList<ProcessSnapshot> Capture()
    {
        Interlocked.Increment(ref InventoryCaptureCount);
        lock (_inventoryGate)
        {
            return Running.ToArray().Select(identity =>
                new ProcessSnapshot(identity.RuntimeKey.ProcessId, "Synthetic", identity.RuntimeKey.StartedAtUtc,
                    identity.ExecutablePath, identity.SessionId, 0, TimeSpan.Zero, false,
                    Priorities.GetValueOrDefault(identity.RuntimeKey, ProcessPriorityClass.Normal).ToString())).ToArray();
        }
    }

    IReadOnlyDictionary<int, int> IProcessParentMapProvider.Capture() => new Dictionary<int, int>(Parents);

    IReadOnlyList<CpuProcessSample> ICpuProcessSource.Capture() => Running.ToArray()
        .Select(identity => new CpuProcessSample(identity.RuntimeKey.ProcessId, "Synthetic",
            identity.RuntimeKey.StartedAtUtc, TimeSpan.Zero, Parents.GetValueOrDefault(identity.RuntimeKey.ProcessId),
            identity.SessionId)).ToArray();

    public ValueTask<ProcessIdentity?> TryCaptureAsync(int processId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Running.ToArray().FirstOrDefault(identity => identity.RuntimeKey.ProcessId == processId));

    public ValueTask<ProcessIdentity?> TryCaptureAsync(Process process, CancellationToken cancellationToken) =>
        TryCaptureAsync(process.Id, cancellationToken);

    public ValueTask<bool> MatchesRuntimeIdentityAsync(ProcessIdentity expectedIdentity, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Running.ToArray().Any(expectedIdentity.MatchesRuntimeIdentity));

    private sealed class FakePriorityAction(PlannedBackgroundApplication application, SyntheticSession owner)
        : IReversibleAction<RuntimeProcessPriorityState>
    {
        public ActionDescriptor Descriptor { get; } = new(application.ActionId, SystemTargetKind.Process,
            application.Identity.RuntimeKey.ToString(), OptimizationActionKind.ReduceProcessPriority);
        private RuntimeProcessPriorityState Current => new(true,
            owner.Priorities.GetValueOrDefault(application.Identity.RuntimeKey, ProcessPriorityClass.Normal));
        public ValueTask<PreparedAction<RuntimeProcessPriorityState>> PrepareAsync(ActionExecutionContext context, CancellationToken token) =>
            ValueTask.FromResult(new PreparedAction<RuntimeProcessPriorityState>(Current,
                new(true, ProcessPriorityClass.BelowNormal), DateTimeOffset.UtcNow)
            { IsUnchanged = Current.PriorityClass == ProcessPriorityClass.BelowNormal });
        public ValueTask<ActionValidationResult> ValidateAsync(ActionExecutionContext context,
            PreparedAction<RuntimeProcessPriorityState> prepared, CancellationToken token) => ValueTask.FromResult(ActionValidationResult.Allowed());
        public ValueTask ApplyAsync(ActionExecutionContext context, PreparedAction<RuntimeProcessPriorityState> prepared, CancellationToken token)
        {
            owner.Priorities[application.Identity.RuntimeKey] = prepared.DesiredState.PriorityClass!.Value;
            Interlocked.Increment(ref owner.PrioritySetCount);
            return ValueTask.CompletedTask;
        }
        public ValueTask<ActionVerificationResult> VerifyAsync(ActionExecutionContext context,
            PreparedAction<RuntimeProcessPriorityState> prepared, CancellationToken token) => ValueTask.FromResult(ActionVerificationResult.Verified());
        public ValueTask<RuntimeProcessPriorityState> ReadCurrentStateAsync(ActionExecutionContext context, CancellationToken token) => ValueTask.FromResult(Current);
        public ValueTask CompensateAsync(ActionExecutionContext context, PreparedAction<RuntimeProcessPriorityState> prepared, CancellationToken token)
        {
            owner.Priorities[application.Identity.RuntimeKey] = prepared.OriginalState.PriorityClass!.Value;
            return ValueTask.CompletedTask;
        }
        public ValueTask<ActionVerificationResult> VerifyCompensationAsync(ActionExecutionContext context,
            PreparedAction<RuntimeProcessPriorityState> prepared, CancellationToken token) => ValueTask.FromResult(ActionVerificationResult.Verified());
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_started && await Orchestrator.GetActiveAsync(CancellationToken.None) is not null)
            {
                await Orchestrator.RestoreAsync(Id, CancellationToken.None);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        await Orchestrator.DisposeAsync();
        Journal.Dispose();
        Store.Dispose();
        Directory.Delete(_directory, true);
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Read(ref _seconds);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1).AddSeconds(GetTimestamp());
        internal void Advance(int seconds) => Interlocked.Add(ref _seconds, seconds);
    }
}

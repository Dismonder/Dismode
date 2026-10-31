using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dismode.Contracts.Protocol;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Data.Journal;
using Dismode.Data.UserData;
using Dismode.IntegrationTests.Cpu;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

/// <summary>
/// CPU sets on the session's processes: the background actuator and the
/// orchestrator's restore paths leave a foreign assignment alone, record what
/// they wrote, and undo it only while it is still theirs.
/// </summary>
[TestClass]
public sealed class CpuSetsSessionTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-zbiory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_target is not null)
        {
            try
            {
                if (!_target.HasExited)
                {
                    _target.Kill(entireProcessTree: true);
                    _target.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                _target.Dispose();
                _target = null;
            }
        }

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

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorWritesOnEmptyRecordsTheIdsAndRestoresOnlyWhileTheyAreOurs()
    {
        FakeCpuSetsPort port = new();
        (JournaledProBalanceActuator actuator, RecordingLedger ledger,
            ProcessRuntimeKey key) = await StartAsync(port);

        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));

        CollectionAssert.AreEqual(new uint[] { 0 }, port.Current);
        RestrainedProcessRecord record = ledger.Recorded[^1];
        Assert.IsTrue(record.SteeredCpuSets);
        Assert.IsNotNull(record.CpuSetsTransaction);
        Assert.IsEmpty(record.OriginalCpuSetIds!);
        CollectionAssert.AreEqual(new uint[] { 0 }, record.AppliedCpuSetIds);

        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.IsEmpty(port.Current);
        Assert.AreEqual(2, port.Writes);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorLeavesAForeignAssignmentAlone()
    {
        FakeCpuSetsPort port = new() { Current = [5, 6] };
        (JournaledProBalanceActuator actuator, RecordingLedger ledger,
            ProcessRuntimeKey key) = await StartAsync(port);

        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(0, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 5, 6 }, port.Current);
        Assert.IsFalse(ledger.Recorded[^1].SteeredCpuSets);
        Assert.IsNull(ledger.Recorded[^1].AppliedCpuSetIds);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorDoesNotWriteWhenTheTargetEqualsTheOriginal()
    {
        FakeCpuSetsPort port = new() { Current = [0] };
        (JournaledProBalanceActuator actuator, RecordingLedger ledger,
            ProcessRuntimeKey key) = await StartAsync(port);

        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(0, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 0 }, port.Current);
        Assert.IsFalse(ledger.Recorded[^1].SteeredCpuSets);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorSkipsWhenTheAssignmentCannotBeRead()
    {
        FakeCpuSetsPort port = new() { QueryFails = true };
        (JournaledProBalanceActuator actuator, RecordingLedger ledger,
            ProcessRuntimeKey key) = await StartAsync(port);

        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(0, port.Writes);
        Assert.IsFalse(ledger.Recorded[^1].SteeredCpuSets);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorTreatsAMismatchedReadBackAsNotApplied()
    {
        FakeCpuSetsPort port = new() { ReadBackAfterApply = [9] };
        (JournaledProBalanceActuator actuator, RecordingLedger ledger,
            ProcessRuntimeKey key) = await StartAsync(port);

        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        Assert.IsFalse(ledger.Recorded[^1].SteeredCpuSets);
        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(1, port.Writes, "Po niezgodnym odczycie nic nie cofamy.");
        CollectionAssert.AreEqual(new uint[] { 9 }, port.Current);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorLeavesAnAssignmentChangedByOthersBeforeRelease()
    {
        FakeCpuSetsPort port = new();
        (JournaledProBalanceActuator actuator, _, ProcessRuntimeKey key) =
            await StartAsync(port);
        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        port.Current = [7];

        await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(1, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 7 }, port.Current);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ActuatorDoesNotTouchSetsWhenThePidBelongsToAnotherProcess()
    {
        FakeCpuSetsPort port = new();
        ReusablePidIdentityProvider identities = new();
        (JournaledProBalanceActuator actuator, _, ProcessRuntimeKey key) =
            await StartAsync(port, identities);
        Assert.IsTrue(await actuator.RestrainAsync(key, CancellationToken.None));
        identities.PidReused = true;

        _ = await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.AreEqual(1, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 0 }, port.Current);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RecordedBackgroundSetsAreRestoredOnlyWhenTheyMatch()
    {
        _target = StartSleeper();
        ProcessIdentity identity = await Capture(_target);
        FakeCpuSetsPort port = new() { Current = [3, 4] };
        await using LocalGameSessionOrchestrator orchestrator =
            CreateOrchestrator(port, new ProcessIdentityProvider());

        // Zbiory zmienione przez kogos po nas: zostaja.
        await orchestrator.ClearSteeredCpuSetsAsync(
            Application(identity, original: [], applied: [0]),
            CancellationToken.None);
        Assert.AreEqual(0, port.Writes);
        CollectionAssert.AreEqual(new uint[] { 3, 4 }, port.Current);

        // Dokladnie to, co zapisalismy (inna kolejnosc): wraca oryginal.
        port.Current = [4, 3];
        await orchestrator.ClearSteeredCpuSetsAsync(
            Application(identity, original: [], applied: [3, 4]),
            CancellationToken.None);
        Assert.IsEmpty(port.Current);
        Assert.AreEqual(1, port.Writes);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task LegacyRecordWithOnlyTheFlagStillClearsUnconditionally()
    {
        _target = StartSleeper();
        ProcessIdentity identity = await Capture(_target);
        FakeCpuSetsPort port = new() { Current = [3, 4] };
        await using LocalGameSessionOrchestrator orchestrator =
            CreateOrchestrator(port, new ProcessIdentityProvider());

        await orchestrator.ClearSteeredCpuSetsAsync(
            Application(identity, original: null, applied: null),
            CancellationToken.None);

        Assert.IsEmpty(port.Current, "Stary rekord czyscil bezwarunkowo i nadal ma.");
        Assert.AreEqual(1, port.Writes);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task BackgroundRestoreSkipsAReusedPid()
    {
        _target = StartSleeper();
        ProcessIdentity identity = await Capture(_target);
        FakeCpuSetsPort port = new() { Current = [0] };
        ReusablePidIdentityProvider identities = new() { PidReused = true };
        await using LocalGameSessionOrchestrator orchestrator =
            CreateOrchestrator(port, identities);

        await orchestrator.ClearSteeredCpuSetsAsync(
            Application(identity, original: [], applied: [0]),
            CancellationToken.None);
        await orchestrator.ClearSteeredCpuSetsAsync(
            Application(identity, original: null, applied: null),
            CancellationToken.None);

        Assert.AreEqual(0, port.Writes);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task GameCpuSetsAreRestoredOnlyWhileTheyAreOurs()
    {
        _target = StartSleeper();
        ProcessIdentity identity = await Capture(_target);
        FakeCpuSetsPort port = new() { Current = [8] };
        ReusablePidIdentityProvider identities = new();
        await using LocalGameSessionOrchestrator orchestrator =
            CreateOrchestrator(port, identities);
        LocalGameSessionOrchestrator.GameCpuSetsRecoveryMetadata written =
            new([], [1, 2]);

        // Nic nie zapisano: nic do cofniecia.
        await orchestrator.RestoreGameCpuSetsAsync(identity, null);
        // Przypisanie zmienione przez launcher: zostaje.
        await orchestrator.RestoreGameCpuSetsAsync(identity, written);
        Assert.AreEqual(0, port.Writes);

        // Ten sam numer, inny proces: zostaje, nawet gdy zbiory pasuja.
        port.Current = [2, 1];
        identities.PidReused = true;
        await orchestrator.RestoreGameCpuSetsAsync(identity, written);
        Assert.AreEqual(0, port.Writes);

        // Zbiory nadal nasze i ten sam proces: wraca oryginal.
        identities.PidReused = false;
        await orchestrator.RestoreGameCpuSetsAsync(identity, written);
        Assert.IsEmpty(port.Current);
        Assert.AreEqual(1, port.Writes);
    }

    [TestMethod]
    public void CheckpointWrittenBeforeTheIdsExistedStillDeserializes()
    {
        // Rekord z flaga SteeredCpuSets, ale bez list zbiorow: tak wygladaja
        // punkty kontrolne sprzed tej zmiany.
        Type metadataType = typeof(LocalGameSessionOrchestrator)
            .GetNestedType(
                "BackgroundApplicationRecoveryMetadata",
                BindingFlags.NonPublic)!;
        JsonSerializerOptions options = (JsonSerializerOptions)typeof(
                LocalGameSessionOrchestrator)
            .GetField(
                "SerializerOptions",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        ProcessIdentity identity = new ProcessIdentityProvider()
            .TryCaptureAsync(Environment.ProcessId, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            ?? throw new AssertFailedException("Brak tozsamosci procesu testowego.");
        JsonObject legacy = new()
        {
            ["ActionId"] = Guid.NewGuid(),
            ["IdempotencyKey"] = Guid.NewGuid(),
            ["DisplayName"] = "Stary",
            ["Identity"] = JsonSerializer.SerializeToNode(identity, options),
            ["ActionMode"] = JsonSerializer.SerializeToNode(
                BackgroundProcessActionMode.LowerPriority,
                options),
            ["EstimatedWorkingSetBytes"] = 0,
            ["SteeredCpuSets"] = true,
        };

        object restored = JsonSerializer.Deserialize(
            legacy.ToJsonString(options),
            metadataType,
            options)!;

        Assert.AreEqual(
            true,
            metadataType.GetProperty("SteeredCpuSets")!.GetValue(restored));
        Assert.IsNull(metadataType.GetProperty("OriginalCpuSetIds")!.GetValue(restored));
        Assert.IsNull(metadataType.GetProperty("AppliedCpuSetIds")!.GetValue(restored));
    }

    private static PlannedBackgroundApplication Application(
        ProcessIdentity identity,
        uint[]? original,
        uint[]? applied) =>
        new(
            new ActionId(Guid.NewGuid()),
            IdempotencyKey.Create(),
            "Test",
            identity,
            BackgroundProcessActionMode.LowerPriority,
            EcoQosActionId: null,
            EcoQosIdempotencyKey: null,
            AffinityActionId: null,
            AffinityIdempotencyKey: null,
            IoPriorityActionId: null,
            IoPriorityIdempotencyKey: null,
            MemoryPriorityActionId: null,
            MemoryPriorityIdempotencyKey: null,
            RestartDescriptor: null,
            EstimatedWorkingSetBytes: 0,
            SteeredCpuSets: true,
            OriginalCpuSetIds: original,
            AppliedCpuSetIds: applied);

    private static async Task<ProcessIdentity> Capture(Process process) =>
        await new ProcessIdentityProvider()
            .TryCaptureAsync(process, CancellationToken.None)
        ?? throw new AssertFailedException("Brak tozsamosci procesu testowego.");

    private LocalGameSessionOrchestrator CreateOrchestrator(
        FakeCpuSetsPort port,
        IProcessIdentityProvider identities)
    {
        SqliteUserDataStore store = new(Path.Combine(_directory, "user.db"));
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        return new LocalGameSessionOrchestrator(
            store,
            store,
            journal,
            identityProvider: identities)
        {
            CpuSetsPort = port,
        };
    }

    private async Task<(JournaledProBalanceActuator Actuator,
        RecordingLedger Ledger,
        ProcessRuntimeKey Key)> StartAsync(
            FakeCpuSetsPort port,
            IProcessIdentityProvider? identities = null)
    {
        _target = StartSleeper();
        ProcessRuntimeKey key = new(
            _target.Id,
            _target.StartTime.ToUniversalTime());
        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            identities,
            backgroundCpuSetIds: [0],
            backgroundAffinityMask: 0,
            lowerBackgroundIoPriority: false)
        {
            CpuSetsPort = port,
        };
        RecordingLedger ledger = new();
        actuator.AttachLedger(ledger);
        return (actuator, ledger, key);
    }

    private static Process StartSleeper()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 120");
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        Thread.Sleep(800);
        process.Refresh();
        return process;
    }

    private sealed class RecordingLedger : IRestraintLedger
    {
        public List<RestrainedProcessRecord> Recorded { get; } = [];

        public ValueTask RecordAsync(
            RestrainedProcessRecord record,
            CancellationToken cancellationToken)
        {
            Recorded.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask ForgetAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Real identities until <see cref="PidReused"/> is set; from then on
    /// every stored identity "no longer matches", as after PID reuse.
    /// </summary>
    private sealed class ReusablePidIdentityProvider : IProcessIdentityProvider
    {
        private readonly ProcessIdentityProvider _inner = new();

        public bool PidReused { get; set; }

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            int processId,
            CancellationToken cancellationToken) =>
            _inner.TryCaptureAsync(processId, cancellationToken);

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            Process process,
            CancellationToken cancellationToken) =>
            _inner.TryCaptureAsync(process, cancellationToken);

        public ValueTask<bool> MatchesRuntimeIdentityAsync(
            ProcessIdentity expectedIdentity,
            CancellationToken cancellationToken) =>
            PidReused
                ? ValueTask.FromResult(false)
                : _inner.MatchesRuntimeIdentityAsync(
                    expectedIdentity,
                    cancellationToken);
    }
}

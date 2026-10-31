using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Data.Journal;
using Dismode.Windows.NativeInterop;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Processes;

[TestClass]
public sealed class EcoQosTests
{
    [TestMethod]
    [DataRow(0u, 0u)]
    [DataRow(1u, 1u)]
    [DataRow(1u, 0u)]
    public async Task EcoQosRestoresExactNativeMasks(uint control, uint state)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        WriteNative(harness.Process, control | 4u, state | 4u);
        ProcessPowerThrottlingState original = ReadNative(harness.Process);
        Assert.AreEqual(control | 4u, original.ControlMask);
        Assert.AreEqual(state | 4u, original.StateMask);
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recovery.jsonl");
            ActionId actionId = ActionId.Create();
            ActionExecutionContext context = CreateContext(actionId);
            ProcessEcoQosAction action = new(actionId, identity);
            using (AppendOnlyRecoveryJournal journal = new(path))
            {
                ActionExecutionResult result = await new TransactionCoordinator<ProcessEcoQosState>(journal)
                    .ExecuteAsync(action, context, CancellationToken.None);
                Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, result.Status, result.Details);
                AssertMasks(harness.Process, 5u, 5u);
            }

            // Odtworzenie z dysku: nowa instancja akcji nie ma snapshotu w pamieci.
            using AppendOnlyRecoveryJournal reopened = new(path);
            ActionRecoveryResult restored = await new ActionRecoveryCoordinator<ProcessEcoQosState>(reopened)
                .RecoverAsync(new ProcessEcoQosAction(actionId, identity), context, CancellationToken.None);
            Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status, restored.Details);
            AssertMasks(harness.Process, original.ControlMask, original.StateMask);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(0u, 0u)]
    [DataRow(1u, 1u)]
    [DataRow(1u, 0u)]
    public async Task RuntimeEcoQosRestoresExactNativeMasks(uint control, uint state)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        WriteNative(harness.Process, control | 4u, state | 4u);
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recovery.jsonl");
            ActionId actionId = ActionId.Create();
            ActionExecutionContext context = CreateContext(actionId);
            using (AppendOnlyRecoveryJournal journal = new(path))
            {
                ActionExecutionResult result = await new TransactionCoordinator<RuntimeProcessEcoQosState>(journal)
                    .ExecuteAsync(new RuntimeProcessEcoQosAction(actionId, identity), context, CancellationToken.None);
                Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, result.Status, result.Details);
                AssertMasks(harness.Process, 5u, 5u);
            }

            using AppendOnlyRecoveryJournal reopened = new(path);
            ActionRecoveryResult restored = await new ActionRecoveryCoordinator<RuntimeProcessEcoQosState>(reopened)
                .RecoverAsync(new RuntimeProcessEcoQosAction(actionId, identity), context, CancellationToken.None);
            Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status, restored.Details);
            AssertMasks(harness.Process, control | 4u, state | 4u);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EcoQosExternalChangeIsReportedAsConflict(bool runtime)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        WriteNative(harness.Process, 0, 0);
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);
        string directory = CreateDirectory();
        try
        {
            using AppendOnlyRecoveryJournal journal = new(Path.Combine(directory, "recovery.jsonl"));
            ActionRecoveryResult restored;
            if (runtime)
            {
                RuntimeProcessEcoQosAction action = new(actionId, identity);
                ActionExecutionResult applied = await new TransactionCoordinator<RuntimeProcessEcoQosState>(journal)
                    .ExecuteAsync(action, context, CancellationToken.None);
                Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, applied.Status);
                WriteNative(harness.Process, 1u, 0u);
                restored = await new ActionRecoveryCoordinator<RuntimeProcessEcoQosState>(journal)
                    .RecoverAsync(action, context, CancellationToken.None);
            }
            else
            {
                ProcessEcoQosAction action = new(actionId, identity);
                ActionExecutionResult applied = await new TransactionCoordinator<ProcessEcoQosState>(journal)
                    .ExecuteAsync(action, context, CancellationToken.None);
                Assert.AreEqual(ActionExecutionStatus.AppliedAndVerified, applied.Status);
                WriteNative(harness.Process, 1u, 0u);
                restored = await new ActionRecoveryCoordinator<ProcessEcoQosState>(journal)
                    .RecoverAsync(action, context, CancellationToken.None);
            }

            Assert.AreEqual(ActionRecoveryStatus.ConflictRequiresDecision, restored.Status);
            AssertMasks(harness.Process, 1u, 0u);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "SystemManaged")]
    [DataRow(true, "On")]
    public void EcoQosLegacyRecordsDeserialize(bool enabled, string expectedMode)
    {
        string json = $$"""{"ExecutionSpeedThrottled":{{enabled.ToString().ToLowerInvariant()}}}""";
        ProcessEcoQosState state = JsonSerializer.Deserialize<ProcessEcoQosState>(json)!;
        AssertSerializedMode(state, expectedMode);
        RuntimeProcessEcoQosState runtime = JsonSerializer.Deserialize<RuntimeProcessEcoQosState>(
            $$"""{"IsRunning":true,"ExecutionSpeedThrottled":{{enabled.ToString().ToLowerInvariant()}}}""")!;
        AssertSerializedMode(runtime, expectedMode);
    }

    [TestMethod]
    [DataRow(EcoQosMode.SystemManaged)]
    [DataRow(EcoQosMode.On)]
    [DataRow(EcoQosMode.Off)]
    public void EcoQosVersionedRecordsRoundTrip(EcoQosMode mode)
    {
        ProcessEcoQosState state = new(mode);
        string json = JsonSerializer.Serialize(state);
        Assert.AreEqual(2, JsonSerializer.SerializeToElement(state).GetProperty("SchemaVersion").GetInt32());
        Assert.AreEqual(state, JsonSerializer.Deserialize<ProcessEcoQosState>(json));
        RuntimeProcessEcoQosState runtime = new(true, mode);
        Assert.AreEqual(runtime, JsonSerializer.Deserialize<RuntimeProcessEcoQosState>(JsonSerializer.Serialize(runtime)));
    }

    [TestMethod]
    public void EcoQosLegacyMissingProcessDeserializes()
    {
        RuntimeProcessEcoQosState? state = JsonSerializer.Deserialize<RuntimeProcessEcoQosState>(
            """{"IsRunning":false,"ExecutionSpeedThrottled":null}""");
        Assert.AreEqual(new RuntimeProcessEcoQosState(false, null), state);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EcoQosRecoversLegacyJournalAfterUnconfirmedApply(bool runtime)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "recovery.jsonl");
            using (AppendOnlyRecoveryJournal journal = new(path))
            {
                string prefix = runtime ? "\"IsRunning\":true," : string.Empty;
                foreach (JournalEventKind kind in new[] { JournalEventKind.ActionPrepared, JournalEventKind.ActionApplying })
                {
                    await journal.AppendDurableAsync(new(
                        context.SessionId.Value, actionId.Value, context.IdempotencyKey.Value,
                        kind, "Process", identity.RuntimeKey.ToString(),
                        "{" + prefix + "\"ExecutionSpeedThrottled\":false}",
                        "{" + prefix + "\"ExecutionSpeedThrottled\":true}",
                        null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
                }

                // Awaria po mutacji, przed ActionApplied: pozostaje tylko stary snapshot bool.
                WriteNative(harness.Process, 5u, 5u);
            }

            using AppendOnlyRecoveryJournal reopened = new(path);
            ActionRecoveryResult restored = runtime
                ? await new ActionRecoveryCoordinator<RuntimeProcessEcoQosState>(reopened)
                    .RecoverAsync(new RuntimeProcessEcoQosAction(actionId, identity), context, CancellationToken.None)
                : await new ActionRecoveryCoordinator<ProcessEcoQosState>(reopened)
                    .RecoverAsync(new ProcessEcoQosAction(actionId, identity), context, CancellationToken.None);
            Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status, restored.Details);
            AssertMasks(harness.Process, 4u, 4u);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(EcoQosMode.SystemManaged, 0u, 0u)]
    [DataRow(EcoQosMode.On, 1u, 1u)]
    [DataRow(EcoQosMode.Off, 1u, 0u)]
    public void EcoQosPreservesUnknownBitsAndIgnoresUncontrolledState(
        EcoQosMode mode, uint control, uint state)
    {
        // Rozne obce bity w obu maskach; nie wysylamy nieobslugiwanych bitow do Windows.
        ProcessPowerThrottlingState native = new()
        {
            Version = 1,
            ControlMask = 0x80000004u,
            StateMask = 0x40000001u,
        };
        Assert.AreEqual(EcoQosMode.SystemManaged, ProcessPowerThrottlingController.ReadMode(native));
        ProcessPowerThrottlingState updated = ProcessPowerThrottlingController.WithMode(native, mode);
        Assert.AreEqual(0x80000004u | control, updated.ControlMask);
        Assert.AreEqual(0x40000000u | state, updated.StateMask);
        Assert.AreEqual(native.Version, updated.Version);
        Assert.AreEqual(mode, ProcessPowerThrottlingController.ReadMode(updated));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EcoQosChecksCurrentModeImmediatelyBeforeMutation(bool runtime)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        WriteNative(harness.Process, 0, 0);
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);
        if (runtime)
        {
            RuntimeProcessEcoQosAction action = new(actionId, identity);
            PreparedAction<RuntimeProcessEcoQosState> prepared =
                await action.PrepareAsync(context, CancellationToken.None);
            WriteNative(harness.Process, 1u, 0u);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                action.ApplyAsync(context, prepared, CancellationToken.None).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                action.CompensateAsync(context, prepared, CancellationToken.None).AsTask());
        }
        else
        {
            ProcessEcoQosAction action = new(actionId, identity);
            PreparedAction<ProcessEcoQosState> prepared =
                await action.PrepareAsync(context, CancellationToken.None);
            WriteNative(harness.Process, 1u, 0u);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                action.ApplyAsync(context, prepared, CancellationToken.None).AsTask());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                action.CompensateAsync(context, prepared, CancellationToken.None).AsTask());
        }

        AssertMasks(harness.Process, 1u, 0u);
    }

    [TestMethod]
    public async Task EcoQosRefusesReusedProcessId()
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        ProcessIdentity reusedPid = new(
            new(identity.RuntimeKey.ProcessId, identity.RuntimeKey.StartedAtUtc.AddTicks(1)),
            identity.ExecutablePath, identity.ExecutableSha256, identity.Publisher,
            identity.UserSid, identity.SessionId, identity.ParentRuntimeKey);
        ProcessPowerThrottlingState original = ReadNative(harness.Process);
        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);
        ProcessEcoQosAction action = new(actionId, reusedPid);
        RuntimeProcessEcoQosAction runtime = new(actionId, reusedPid);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            action.PrepareAsync(context, CancellationToken.None).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            runtime.PrepareAsync(context, CancellationToken.None).AsTask());
        RuntimeProcessEcoQosState current = await runtime.ReadCurrentStateAsync(context, CancellationToken.None);
        Assert.IsFalse(current.IsRunning);
        Assert.AreEqual(RestoreDecisionKind.NoActionAlreadyOriginal, runtime.DecideRecovery(
            new(true, EcoQosMode.SystemManaged), new(true, EcoQosMode.On), current).Kind);
        AssertMasks(harness.Process, original.ControlMask, original.StateMask);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EcoQosPreservesForeignChangeDuringSession(bool runtime)
    {
        await using ProcessHarnessFixture harness = await ProcessHarnessFixture.StartAsync();
        WriteNative(harness.Process, 4u, 4u);
        ProcessIdentity identity = await CaptureAsync(harness.Process);
        ActionId actionId = ActionId.Create();
        ActionExecutionContext context = CreateContext(actionId);
        if (runtime)
        {
            RuntimeProcessEcoQosAction action = new(actionId, identity);
            PreparedAction<RuntimeProcessEcoQosState> prepared =
                await action.PrepareAsync(context, CancellationToken.None);
            await action.ApplyAsync(context, prepared, CancellationToken.None);
            WriteNative(harness.Process, 5u, 1u);
            await action.CompensateAsync(context, prepared, CancellationToken.None);
        }
        else
        {
            ProcessEcoQosAction action = new(actionId, identity);
            PreparedAction<ProcessEcoQosState> prepared =
                await action.PrepareAsync(context, CancellationToken.None);
            await action.ApplyAsync(context, prepared, CancellationToken.None);
            WriteNative(harness.Process, 5u, 1u);
            await action.CompensateAsync(context, prepared, CancellationToken.None);
        }

        AssertMasks(harness.Process, 4u, 0u);
    }

    private static void AssertSerializedMode<T>(T state, string expectedMode)
    {
        JsonElement json = JsonSerializer.SerializeToElement(state);
        Assert.IsTrue(json.TryGetProperty("Mode", out JsonElement mode), "Brak pełnego trybu EcoQoS w rekordzie.");
        Assert.AreEqual(expectedMode, mode.GetString());
    }

    private static async Task<ProcessIdentity> CaptureAsync(Process process) =>
        await new ProcessIdentityProvider().TryCaptureAsync(process, CancellationToken.None)
        ?? throw new AssertFailedException("Nie można odczytać tożsamości procesu pomocniczego.");

    private static ActionExecutionContext CreateContext(ActionId actionId) =>
        new(SessionId.Create(), actionId, IdempotencyKey.Create(), DateTimeOffset.UtcNow);

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Dismode.EcoQosTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertMasks(Process process, uint control, uint state)
    {
        ProcessPowerThrottlingState current = ReadNative(process);
        Assert.AreEqual(control, current.ControlMask, "ControlMask");
        Assert.AreEqual(state, current.StateMask, "StateMask");
    }

    private static ProcessPowerThrottlingState ReadNative(Process process)
    {
        ProcessPowerThrottlingState state = new() { Version = 1 };
        if (ProcessNativeMethods.GetProcessInformation(process.SafeHandle,
                ProcessInformationClass.ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()) == 0)
        {
            throw new Win32Exception();
        }

        return state;
    }

    private static void WriteNative(Process process, uint control, uint state)
    {
        ProcessPowerThrottlingState native = new() { Version = 1, ControlMask = control, StateMask = state };
        if (ProcessNativeMethods.SetProcessInformation(process.SafeHandle,
                ProcessInformationClass.ProcessPowerThrottling, in native,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()) == 0)
        {
            throw new Win32Exception();
        }
    }
}

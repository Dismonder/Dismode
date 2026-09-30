using System.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Data.Journal;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class ProcessActionTests
{
    [TestMethod]
    public async Task IdentityProviderCapturesStableExecutableIdentity()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentityProvider provider = new();

        ProcessIdentity? first = await provider.TryCaptureAsync(
            harness.Process,
            CancellationToken.None);
        ProcessIdentity? second = await provider.TryCaptureAsync(
            harness.Process,
            CancellationToken.None);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.IsTrue(first.MatchesExecutable(second));
        Assert.AreEqual(harness.Process.Id, first.RuntimeKey.ProcessId);
        Assert.HasCount(64, first.ExecutableSha256);
        StringAssert.EndsWith(
            first.ExecutablePath,
            "Dismode.ProcessTestHarness.exe",
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RuntimeIdentityCheckDoesNotRequireRepeatedFileHashing()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentityProvider provider = new();
        ProcessIdentity captured = await CaptureRequiredIdentityAsync(
            harness.Process);
        ProcessIdentity sameRuntimeWithDifferentHash = new(
            captured.RuntimeKey,
            captured.ExecutablePath,
            new string('0', 64),
            captured.Publisher,
            captured.UserSid,
            captured.SessionId,
            captured.ParentRuntimeKey);
        ProcessIdentity reusedPid = new(
            new(
                captured.RuntimeKey.ProcessId,
                captured.RuntimeKey.StartedAtUtc.AddTicks(1)),
            sameRuntimeWithDifferentHash.ExecutablePath,
            sameRuntimeWithDifferentHash.ExecutableSha256,
            sameRuntimeWithDifferentHash.Publisher,
            sameRuntimeWithDifferentHash.UserSid,
            sameRuntimeWithDifferentHash.SessionId,
            sameRuntimeWithDifferentHash.ParentRuntimeKey);

        bool sameRuntime = await provider.MatchesRuntimeIdentityAsync(
            sameRuntimeWithDifferentHash,
            CancellationToken.None);
        bool wrongRuntime = await provider.MatchesRuntimeIdentityAsync(
            reusedPid,
            CancellationToken.None);

        Assert.IsTrue(sameRuntime);
        Assert.IsFalse(wrongRuntime);
    }

    [TestMethod]
    public async Task PriorityActionUsesJournalAndRestoresOriginalState()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        harness.Process.PriorityClass = ProcessPriorityClass.Normal;

        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ActionId actionId = ActionId.Create();
        ProcessPriorityAction action = new(
            actionId,
            identity,
            ProcessPriorityClass.BelowNormal);
        ActionExecutionContext context = CreateContext(actionId);

        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<ProcessPriorityState> transaction = new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status,
            execution.Details);
        Assert.AreEqual(
            ProcessPriorityClass.BelowNormal,
            ReadPriority(harness.Process.Id));

        ActionRecoveryCoordinator<ProcessPriorityState> recovery = new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);
        ActionRecoveryResult repeated = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(
            ActionRecoveryStatus.AlreadyRestored,
            repeated.Status);
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            ReadPriority(harness.Process.Id));
    }

    [TestMethod]
    public async Task HighGamePriorityIsVisibleThroughWindowsAndRestores()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        harness.Process.PriorityClass = ProcessPriorityClass.Normal;

        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ActionId actionId = ActionId.Create();
        RuntimeProcessPriorityAction action = new(
            actionId,
            identity,
            ProcessPriorityClass.High);
        ActionExecutionContext context = CreateContext(actionId);
        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<RuntimeProcessPriorityState> transaction =
            new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);
        ProcessPriorityClass independentlyReadPriority =
            ReadPriority(harness.Process.Id);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status,
            execution.Details);
        Assert.AreEqual(
            ProcessPriorityClass.High,
            independentlyReadPriority);

        ActionRecoveryCoordinator<RuntimeProcessPriorityState> recovery =
            new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            ReadPriority(harness.Process.Id));
    }

    [TestMethod]
    public async Task EcoQosActionAppliesAndRestoresOriginalState()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ActionId actionId = ActionId.Create();
        ProcessEcoQosAction action = new(actionId, identity);
        ActionExecutionContext context = CreateContext(actionId);

        ProcessEcoQosState original = await action.ReadCurrentStateAsync(
            context,
            CancellationToken.None);
        Assert.IsFalse(original.ExecutionSpeedThrottled);

        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<ProcessEcoQosState> transaction = new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);
        ProcessEcoQosState applied = await action.ReadCurrentStateAsync(
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status);
        Assert.IsTrue(applied.ExecutionSpeedThrottled);

        ActionRecoveryCoordinator<ProcessEcoQosState> recovery = new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);
        ProcessEcoQosState final = await action.ReadCurrentStateAsync(
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(original, final);
    }

    [TestMethod]
    public async Task RuntimeEcoQosActionUsesJournalAndRestoresOriginalState()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ActionId actionId = ActionId.Create();
        RuntimeProcessEcoQosAction action = new(actionId, identity);
        ActionExecutionContext context = CreateContext(actionId);

        RuntimeProcessEcoQosState original =
            await action.ReadCurrentStateAsync(
                context,
                CancellationToken.None);

        Assert.IsTrue(original.IsRunning);
        Assert.AreEqual(false, original.ExecutionSpeedThrottled);

        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<RuntimeProcessEcoQosState> transaction =
            new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);
        RuntimeProcessEcoQosState applied =
            await action.ReadCurrentStateAsync(
                context,
                CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status,
            execution.Details);
        Assert.IsTrue(applied.IsRunning);
        Assert.AreEqual(true, applied.ExecutionSpeedThrottled);

        ActionRecoveryCoordinator<RuntimeProcessEcoQosState> recovery =
            new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);
        ActionRecoveryResult repeated = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);
        RuntimeProcessEcoQosState final =
            await action.ReadCurrentStateAsync(
                context,
                CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.AreEqual(
            ActionRecoveryStatus.AlreadyRestored,
            repeated.Status);
        Assert.AreEqual(original, final);
    }

    [TestMethod]
    public async Task PriorityActionRevalidatesIdentityImmediatelyBeforeApply()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        harness.Process.PriorityClass = ProcessPriorityClass.Normal;
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ProcessIdentity changedIdentity = new(
            identity.RuntimeKey,
            identity.ExecutablePath,
            new string('0', 64),
            identity.Publisher,
            identity.UserSid,
            identity.SessionId,
            identity.ParentRuntimeKey);
        SequencedIdentityProvider provider = new(
            identity,
            identity,
            changedIdentity);
        ActionId actionId = ActionId.Create();
        ProcessPriorityAction action = new(
            actionId,
            identity,
            ProcessPriorityClass.BelowNormal,
            provider);
        ActionExecutionContext context = CreateContext(actionId);

        PreparedAction<ProcessPriorityState> prepared =
            await action.PrepareAsync(context, CancellationToken.None);
        ActionValidationResult validation = await action.ValidateAsync(
            context,
            prepared,
            CancellationToken.None);

        Assert.IsTrue(validation.IsValid);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => action
                .ApplyAsync(context, prepared, CancellationToken.None)
                .AsTask());
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            ReadPriority(harness.Process.Id));
    }

    [TestMethod]
    public async Task TelemetrySamplerObservesOnlyValidatedProcess()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ProcessTelemetrySampler sampler = new();

        ProcessTelemetrySample first = await sampler.SampleAsync(
            identity,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        ProcessTelemetrySample second = await sampler.SampleAsync(
            identity,
            CancellationToken.None);

        Assert.AreEqual(identity.RuntimeKey, first.RuntimeKey);
        Assert.AreEqual(identity.RuntimeKey, second.RuntimeKey);
        Assert.IsGreaterThanOrEqualTo(0, second.CpuPercent);
        Assert.IsLessThanOrEqualTo(100, second.CpuPercent);
        Assert.IsGreaterThan(0, second.WorkingSetBytes);
        Assert.IsGreaterThan(0, second.ThreadCount);
    }

    [TestMethod]
    public async Task GracefulCloseNeverKillsAndRestartsApprovedApplication()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync();
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        string restartedReadyFile = Path.Combine(
            Path.GetTempPath(),
            $"dismode-restarted-{Guid.NewGuid():N}.ready");
        ApplicationRestartDescriptor restart = new(
            identity.ExecutablePath,
            Path.GetDirectoryName(identity.ExecutablePath)
                ?? throw new AssertFailedException(
                    "The harness executable directory is unavailable."),
            ["--ready-file", restartedReadyFile],
            ApplicationRestartability.Restartable);
        ActionId actionId = ActionId.Create();
        GracefulCloseApplicationAction action = new(
            actionId,
            identity,
            restart);
        ActionExecutionContext context = CreateContext(actionId);

        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<ApplicationProcessState> transaction =
            new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status,
            execution.Details);
        Assert.IsTrue(harness.Process.HasExited);

        ActionRecoveryCoordinator<ApplicationProcessState> recovery =
            new(journal);
        ActionRecoveryResult restored = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, restored.Status);
        Assert.IsNotNull(action.LastRestartedProcessId);
        await WaitForFileAsync(restartedReadyFile);

        using Process restarted = Process.GetProcessById(
            action.LastRestartedProcessId.Value);
        try
        {
            Assert.IsFalse(restarted.HasExited);
            Assert.IsTrue(ProcessWindowHelper.RequestGracefulClose(restarted));
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(3));
            await restarted.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!restarted.HasExited)
            {
                restarted.Kill(entireProcessTree: true);
                await restarted.WaitForExitAsync();
            }

            File.Delete(restartedReadyFile);
        }
    }

    [TestMethod]
    public async Task GracefulCloseTimeoutLeavesApplicationRunning()
    {
        await using ProcessHarnessFixture harness =
            await ProcessHarnessFixture.StartAsync(ignoreClose: true);
        ProcessIdentity identity = await CaptureRequiredIdentityAsync(
            harness.Process);
        ApplicationRestartDescriptor restart = new(
            identity.ExecutablePath,
            Path.GetDirectoryName(identity.ExecutablePath)
                ?? throw new AssertFailedException(
                    "The harness executable directory is unavailable."),
            arguments: null,
            ApplicationRestartability.Restartable);
        ActionId actionId = ActionId.Create();
        GracefulCloseApplicationAction action = new(
            actionId,
            identity,
            restart,
            closeTimeout: TimeSpan.FromMilliseconds(250));
        ActionExecutionContext context = CreateContext(actionId);
        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<ApplicationProcessState> transaction =
            new(journal);

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => transaction
                .ExecuteAsync(action, context, CancellationToken.None)
                .AsTask());

        Assert.IsFalse(harness.Process.HasExited);
        Assert.IsNull(action.LastRestartedProcessId);

        ActionRecoveryCoordinator<ApplicationProcessState> recovery =
            new(journal);
        ActionRecoveryResult result = await recovery.RecoverAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionRecoveryStatus.Restored, result.Status);
        Assert.IsFalse(harness.Process.HasExited);
        Assert.IsNull(action.LastRestartedProcessId);

        harness.Process.Kill(entireProcessTree: true);
        await harness.Process.WaitForExitAsync();
    }

    private static async ValueTask<ProcessIdentity> CaptureRequiredIdentityAsync(
        Process process)
    {
        ProcessIdentity? identity =
            await new ProcessIdentityProvider().TryCaptureAsync(
                process,
                CancellationToken.None);
        return identity
            ?? throw new AssertFailedException(
                "The controlled harness identity could not be captured.");
    }

    private static ActionExecutionContext CreateContext(ActionId actionId) =>
        new(
            SessionId.Create(),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

    private static string CreateJournalPath()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Dismode.IntegrationTests",
            Guid.NewGuid().ToString("N"));
        return Path.Combine(directory, "recovery.jsonl");
    }

    private static ProcessPriorityClass ReadPriority(int processId)
    {
        using Process process = Process.GetProcessById(processId);
        return process.PriorityClass;
    }

    private static async Task WaitForFileAsync(string path)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));
        while (!File.Exists(path))
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                timeout.Token);
        }
    }

    private sealed class SequencedIdentityProvider(
        params ProcessIdentity[] identities) : IProcessIdentityProvider
    {
        private int _nextIdentity = -1;

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            int processId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(
                identities[Math.Min(
                    Interlocked.Increment(ref _nextIdentity),
                    identities.Length - 1)]);

        public ValueTask<ProcessIdentity?> TryCaptureAsync(
            Process process,
            CancellationToken cancellationToken) =>
            TryCaptureAsync(process.Id, cancellationToken);

        public async ValueTask<bool> MatchesRuntimeIdentityAsync(
            ProcessIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            ProcessIdentity? current = await TryCaptureAsync(
                expectedIdentity.RuntimeKey.ProcessId,
                cancellationToken);
            return current is not null
                && expectedIdentity.MatchesRuntimeIdentity(current);
        }
    }
}

internal sealed class ProcessHarnessFixture : IAsyncDisposable
{
    private readonly string _readyFile;

    private ProcessHarnessFixture(Process process, string readyFile)
    {
        Process = process;
        _readyFile = readyFile;
    }

    internal Process Process { get; }

    internal static async ValueTask<ProcessHarnessFixture> StartAsync(
        bool ignoreClose = false)
    {
        string executablePath = FindHarnessExecutable();
        string readyFile = Path.Combine(
            Path.GetTempPath(),
            $"dismode-harness-{Guid.NewGuid():N}.ready");
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFile);
        if (ignoreClose)
        {
            startInfo.ArgumentList.Add("--ignore-close");
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The process test harness did not start.");

        try
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            while (!File.Exists(readyFile))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The process test harness exited with code "
                        + $"{process.ExitCode}.");
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(50),
                    timeout.Token);
            }

            process.Refresh();
            return new(process, readyFile);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                _ = ProcessWindowHelper.RequestGracefulClose(Process);
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(3));
                try
                {
                    await Process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            Process.Dispose();
            File.Delete(_readyFile);
        }
    }

    internal static string FindHarnessExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "The Dismode solution root could not be located.");
        }

        string configuration =
            new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? "Debug";
        string path = Path.Combine(
            directory.FullName,
            "tools",
            "Dismode.ProcessTestHarness",
            "bin",
            configuration,
            "net10.0-windows10.0.26100.0",
            "Dismode.ProcessTestHarness.exe");

        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "Build the process test harness before running integration tests.",
                path);
    }
}

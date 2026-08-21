using System.ServiceProcess;
using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Policies;
using GameShift.Core.Recovery;
using GameShift.Core.Services;
using GameShift.Core.Transactions;
using GameShift.Data.Journal;
using GameShift.Windows.Services;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class ServiceActionTests
{
    [TestMethod]
    public async Task ApprovedServiceUsesJournalAndRestoresWithoutConfigChange()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionId sessionId = SessionId.Create();
        ActionId actionId = ActionId.Create();
        FakeServiceControlAdapter adapter = new(CreateSafeSnapshot());
        StopApprovedServiceAction action = CreateAction(
            actionId,
            sessionId,
            adapter,
            now);
        ActionExecutionContext context = CreateContext(
            sessionId,
            actionId,
            now);
        string journalPath = CreateJournalPath();
        using AppendOnlyRecoveryJournal journal = new(journalPath);
        TransactionCoordinator<WindowsServiceActionState> transaction =
            new(journal);

        ActionExecutionResult execution = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            execution.Status);
        Assert.AreEqual(ServiceControllerStatus.Stopped, adapter.Status);
        Assert.AreEqual(1, adapter.StopCount);

        ActionRecoveryCoordinator<WindowsServiceActionState> recovery =
            new(journal);
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
        Assert.AreEqual(ServiceControllerStatus.Running, adapter.Status);
        Assert.AreEqual(1, adapter.StartCount);
        Assert.AreEqual(
            CreateSafeSnapshot().ConfigurationFingerprint,
            adapter.Snapshot.ConfigurationFingerprint);
    }

    [TestMethod]
    public async Task ProtectedServiceIsBlockedBeforeStop()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionId sessionId = SessionId.Create();
        ActionId actionId = ActionId.Create();
        FakeServiceControlAdapter adapter = new(
            CreateSafeSnapshot(serviceName: "WinDefend"));
        StopApprovedServiceAction action = CreateAction(
            actionId,
            sessionId,
            adapter,
            now,
            serviceName: "WinDefend");
        ActionExecutionContext context = CreateContext(
            sessionId,
            actionId,
            now);
        using AppendOnlyRecoveryJournal journal =
            new(CreateJournalPath());
        TransactionCoordinator<WindowsServiceActionState> transaction =
            new(journal);

        ActionExecutionResult result = await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(ActionExecutionStatus.Blocked, result.Status);
        Assert.AreEqual(0, adapter.StopCount);
        StringAssert.Contains(
            result.Details,
            "protected",
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task UnsafeServiceShapesAreBlocked()
    {
        WindowsServiceSnapshot[] unsafeSnapshots =
        [
            CreateSafeSnapshot(
                serviceType: ServiceType.Win32ShareProcess),
            CreateSafeSnapshot(
                serviceType: ServiceType.KernelDriver),
            CreateSafeSnapshot(triggerCount: 1),
            CreateSafeSnapshot(runningDependents: ["DependentService"]),
        ];

        foreach (WindowsServiceSnapshot snapshot in unsafeSnapshots)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            SessionId sessionId = SessionId.Create();
            ActionId actionId = ActionId.Create();
            FakeServiceControlAdapter adapter = new(snapshot);
            StopApprovedServiceAction action = CreateAction(
                actionId,
                sessionId,
                adapter,
                now);
            ActionExecutionContext context = CreateContext(
                sessionId,
                actionId,
                now);

            PreparedAction<WindowsServiceActionState> prepared =
                await action.PrepareAsync(
                    context,
                    CancellationToken.None);
            ActionValidationResult validation = await action.ValidateAsync(
                context,
                prepared,
                CancellationToken.None);

            Assert.IsFalse(
                validation.IsValid,
                $"Unsafe shape was allowed: {snapshot.ServiceType}, "
                + $"triggers={snapshot.TriggerCount}, "
                + $"dependents={snapshot.RunningDependentServices.Count}");
            Assert.AreEqual(0, adapter.StopCount);
        }
    }

    [TestMethod]
    public async Task ExternalServiceConfigChangeIsPreservedAsConflict()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SessionId sessionId = SessionId.Create();
        ActionId actionId = ActionId.Create();
        FakeServiceControlAdapter adapter = new(CreateSafeSnapshot());
        StopApprovedServiceAction action = CreateAction(
            actionId,
            sessionId,
            adapter,
            now);
        ActionExecutionContext context = CreateContext(
            sessionId,
            actionId,
            now);
        using AppendOnlyRecoveryJournal journal =
            new(CreateJournalPath());
        TransactionCoordinator<WindowsServiceActionState> transaction =
            new(journal);
        await transaction.ExecuteAsync(
            action,
            context,
            CancellationToken.None);
        adapter.ChangeBinaryPath(@"C:\Changed\service.exe");

        ActionRecoveryResult result =
            await new ActionRecoveryCoordinator<WindowsServiceActionState>(
                    journal)
                .RecoverAsync(
                    action,
                    context,
                    CancellationToken.None);

        Assert.AreEqual(
            ActionRecoveryStatus.ConflictRequiresDecision,
            result.Status);
        Assert.AreEqual(0, adapter.StartCount);
        Assert.AreEqual(ServiceControllerStatus.Stopped, adapter.Status);
    }

    private static StopApprovedServiceAction CreateAction(
        ActionId actionId,
        SessionId sessionId,
        FakeServiceControlAdapter adapter,
        DateTimeOffset now,
        string serviceName = "GameShift.TestService") =>
        new(
            actionId,
            serviceName,
            new(
                serviceName,
                sessionId,
                "S-1-5-21-TEST",
                now.AddMinutes(-1),
                now.AddMinutes(10)),
            RecoveryAssurance.CompensationVerified,
            adapter);

    private static ActionExecutionContext CreateContext(
        SessionId sessionId,
        ActionId actionId,
        DateTimeOffset now) =>
        new(
            sessionId,
            actionId,
            IdempotencyKey.Create(),
            now);

    private static WindowsServiceSnapshot CreateSafeSnapshot(
        string serviceName = "GameShift.TestService",
        ServiceType serviceType = ServiceType.Win32OwnProcess,
        int triggerCount = 0,
        IEnumerable<string>? runningDependents = null) =>
        new(
            serviceName,
            "GameShift Test Service",
            ServiceControllerStatus.Running,
            ServiceStartMode.Manual,
            serviceType,
            canStop: true,
            processId: 1234,
            binaryPath: @"C:\GameShift\TestService.exe",
            serviceAccount: "LocalSystem",
            delayedAutoStart: false,
            triggerCount,
            dependencies: [],
            runningDependents ?? []);

    private static string CreateJournalPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "GameShift.ServiceActionTests",
            Guid.NewGuid().ToString("N"),
            "recovery.jsonl");

    private sealed class FakeServiceControlAdapter(
        WindowsServiceSnapshot initialSnapshot) :
        IWindowsServiceControlAdapter
    {
        internal WindowsServiceSnapshot Snapshot { get; private set; } =
            initialSnapshot;

        internal ServiceControllerStatus Status => Snapshot.Status;

        internal int StopCount { get; private set; }

        internal int StartCount { get; private set; }

        public ValueTask<WindowsServiceSnapshot> CaptureAsync(
            string serviceName,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Snapshot);

        public ValueTask RequestStopAsync(
            string serviceName,
            CancellationToken cancellationToken)
        {
            StopCount++;
            Snapshot = CopyWith(
                status: ServiceControllerStatus.Stopped,
                canStop: false,
                processId: 0);
            return ValueTask.CompletedTask;
        }

        public ValueTask RequestStartAsync(
            string serviceName,
            CancellationToken cancellationToken)
        {
            StartCount++;
            Snapshot = CopyWith(
                status: ServiceControllerStatus.Running,
                canStop: true,
                processId: 4321);
            return ValueTask.CompletedTask;
        }

        internal void ChangeBinaryPath(string binaryPath)
        {
            Snapshot = CopyWith(binaryPath: binaryPath);
        }

        private WindowsServiceSnapshot CopyWith(
            ServiceControllerStatus? status = null,
            bool? canStop = null,
            int? processId = null,
            string? binaryPath = null) =>
            new(
                Snapshot.ServiceName,
                Snapshot.DisplayName,
                status ?? Snapshot.Status,
                Snapshot.StartType,
                Snapshot.ServiceType,
                canStop ?? Snapshot.CanStop,
                processId ?? Snapshot.ProcessId,
                binaryPath ?? Snapshot.BinaryPath,
                Snapshot.ServiceAccount,
                Snapshot.DelayedAutoStart,
                Snapshot.TriggerCount,
                Snapshot.Dependencies,
                Snapshot.RunningDependentServices);
    }
}

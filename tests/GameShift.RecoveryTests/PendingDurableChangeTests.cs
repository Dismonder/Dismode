using GameShift.Core.Journal;
using GameShift.Data.Journal;
using GameShift.RecoveryTests.Support;

namespace GameShift.RecoveryTests;

/// <summary>
/// The uninstall gate refuses to remove GameShift while a system change is
/// still outstanding. These tests pin down what actually counts as
/// outstanding, because getting it wrong makes the product impossible to
/// uninstall on machines where nothing was ever changed.
/// </summary>
[TestClass]
public sealed class PendingDurableChangeTests
{
    [TestMethod]
    public async Task MissingJournalHasNothingToRestore()
    {
        using RecoveryTestContext context = new();

        RecoveryJournalInspection inspection =
            await RecoveryJournalInspector.InspectAsync(
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                CancellationToken.None);

        Assert.IsFalse(inspection.HasPendingDurableChanges);
        Assert.AreEqual(0, inspection.RecordCount);
    }

    [TestMethod]
    public async Task AppliedActionWithoutCompensationIsPending()
    {
        using RecoveryTestContext context = new();
        Guid actionId = Guid.NewGuid();

        await WriteAsync(
            context,
            [(JournalEventKind.ActionPrepared, actionId),
             (JournalEventKind.ActionApplied, actionId)]);

        RecoveryJournalInspection inspection = await InspectAsync(context);

        Assert.IsTrue(inspection.HasPendingDurableChanges);
        Assert.AreEqual(1, inspection.PendingDurableActionIds.Count);
        Assert.AreEqual(actionId, inspection.PendingDurableActionIds[0]);
    }

    [TestMethod]
    public async Task CompensatedActionIsNotPending()
    {
        using RecoveryTestContext context = new();
        Guid actionId = Guid.NewGuid();

        await WriteAsync(
            context,
            [(JournalEventKind.ActionApplied, actionId),
             (JournalEventKind.ActionCompensated, actionId)]);

        RecoveryJournalInspection inspection = await InspectAsync(context);

        Assert.IsFalse(inspection.HasPendingDurableChanges);
    }

    [TestMethod]
    public async Task VerifiedCompensationClearsAVerifiedAction()
    {
        using RecoveryTestContext context = new();
        Guid actionId = Guid.NewGuid();

        await WriteAsync(
            context,
            [(JournalEventKind.ActionApplied, actionId),
             (JournalEventKind.ActionVerified, actionId),
             (JournalEventKind.CompensationVerified, actionId)]);

        RecoveryJournalInspection inspection = await InspectAsync(context);

        Assert.IsFalse(inspection.HasPendingDurableChanges);
    }

    [TestMethod]
    public async Task PreparedButNeverAppliedActionIsNotPending()
    {
        using RecoveryTestContext context = new();
        Guid actionId = Guid.NewGuid();

        // Nothing reached the system, so there is nothing to undo.
        await WriteAsync(
            context,
            [(JournalEventKind.ActionPrepared, actionId),
             (JournalEventKind.ActionBlocked, actionId)]);

        RecoveryJournalInspection inspection = await InspectAsync(context);

        Assert.IsFalse(inspection.HasPendingDurableChanges);
    }

    [TestMethod]
    public async Task OneOutstandingActionAmongCompensatedOnesIsReported()
    {
        using RecoveryTestContext context = new();
        Guid restored = Guid.NewGuid();
        Guid outstanding = Guid.NewGuid();

        await WriteAsync(
            context,
            [(JournalEventKind.ActionApplied, restored),
             (JournalEventKind.ActionApplied, outstanding),
             (JournalEventKind.ActionCompensated, restored)]);

        RecoveryJournalInspection inspection = await InspectAsync(context);

        Assert.IsTrue(inspection.HasPendingDurableChanges);
        CollectionAssert.AreEqual(
            new[] { outstanding },
            inspection.PendingDurableActionIds.ToArray());
    }

    private static async Task<RecoveryJournalInspection> InspectAsync(
        RecoveryTestContext context) =>
        await RecoveryJournalInspector.InspectAsync(
            context.JournalPath,
            CancellationToken.None);

    private static async Task WriteAsync(
        RecoveryTestContext context,
        IReadOnlyList<(JournalEventKind Kind, Guid ActionId)> entries)
    {
        using AppendOnlyRecoveryJournal journal = new(context.JournalPath);
        foreach ((JournalEventKind kind, Guid actionId) in entries)
        {
            await journal.AppendDurableAsync(
                new(
                    context.ExecutionContext.SessionId.Value,
                    actionId,
                    Guid.NewGuid(),
                    kind,
                    "OperatingSystemSetting",
                    "target",
                    OriginalStateJson: "{\"value\":\"before\"}",
                    DesiredStateJson: "{\"value\":\"after\"}",
                    CurrentStateJson: null,
                    SessionCheckpoint: null,
                    Details: null,
                    TimestampUtc: DateTimeOffset.UtcNow),
                CancellationToken.None);
        }
    }
}

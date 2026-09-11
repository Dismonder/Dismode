using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Sessions;

/// <summary>
/// One background process the reactive restraint loop has acted on, with the
/// identifiers of every journaled action it applied — enough for the session
/// owner to persist the restraint in its checkpoint and to reverse it later
/// without the actuator's in-memory state.
/// <para>
/// Every action the actuator applies is already journaled, but a journal
/// record alone is not recoverable: after a host crash nobody knows which
/// action ids belong to a restraint that was never released. The session
/// checkpoint is the place that survives a crash and is replayed on the next
/// start, so this is what goes into it.
/// </para>
/// </summary>
public sealed record RestrainedProcessRecord(
    ProcessIdentity Identity,
    string DisplayName,
    ActionId PriorityActionId,
    IdempotencyKey PriorityIdempotencyKey,
    ActionId? AffinityActionId,
    IdempotencyKey? AffinityIdempotencyKey,
    ActionId? IoPriorityActionId,
    IdempotencyKey? IoPriorityIdempotencyKey,
    ActionId? MemoryPriorityActionId,
    IdempotencyKey? MemoryPriorityIdempotencyKey);

/// <summary>
/// Where the reactive restraint loop reports what it has restrained and
/// released, so the session that owns it can make those changes survive a
/// crash of the process that made them.
/// <para>
/// Implementations must not take the session orchestrator's gate: the calls
/// arrive from the restraint loop's own thread, including while the session
/// is being torn down under that gate.
/// </para>
/// </summary>
public interface IRestraintLedger
{
    ValueTask RecordAsync(
        RestrainedProcessRecord record,
        CancellationToken cancellationToken);

    ValueTask ForgetAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// An actuator that can report its restraints to a ledger. Kept separate from
/// the actuator interface so an in-memory actuator used by tests, or one built
/// by a caller-supplied factory, needs no ledger at all. The orchestrator
/// attaches its ledger after obtaining the actuator, whichever way it was made.
/// </summary>
public interface IRestraintLedgerAware
{
    void AttachLedger(IRestraintLedger ledger);
}

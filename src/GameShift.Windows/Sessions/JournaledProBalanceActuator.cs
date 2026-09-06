using System.ComponentModel;
using System.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Journal;
using GameShift.Core.Recovery;
using GameShift.Core.Transactions;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

/// <summary>
/// Carries out ProBalance's decisions through the same transaction machinery
/// as every other change GameShift makes.
/// <para>
/// The in-memory actuator remembers what it changed and undoes it on stop,
/// which is enough while the process is alive. It is not enough if GameShift
/// dies mid-session: the priorities it lowered would stay lowered with nothing
/// left to record that it was us. Going through the journal means the recovery
/// pass on the next start finds them and puts them back.
/// </para>
/// </summary>
public sealed class JournaledProBalanceActuator : IProBalanceActuator
{
    private readonly IRecoveryJournal _journal;
    private readonly SessionId _sessionId;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<ProcessRuntimeKey, RestraintRecord> _applied =
        [];

    public JournaledProBalanceActuator(
        IRecoveryJournal journal,
        SessionId sessionId,
        IProcessIdentityProvider? identityProvider = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _journal = journal;
        _sessionId = sessionId;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<bool> RestrainAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        if (_applied.ContainsKey(runtimeKey))
        {
            return false;
        }

        ProcessIdentity? identity =
            await TryResolveAsync(runtimeKey, cancellationToken)
                .ConfigureAwait(false);
        if (identity is null || !IsWorthLowering(runtimeKey))
        {
            return false;
        }

        ActionId actionId = new(Guid.NewGuid());
        // Kazde ograniczenie to osobna akcja; powtorzenie w obrebie tej
        // samej sesji blokuje slownik powyzej, a po awarii sprawe przejmuje
        // odtwarzanie z journala.
        IdempotencyKey idempotencyKey = IdempotencyKey.Create();
        RuntimeProcessPriorityAction action = new(
            actionId,
            identity,
            ProcessPriorityClass.BelowNormal,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            actionId,
            idempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<RuntimeProcessPriorityState>(
                        _journal)
                    .ExecuteAsync(action, context, cancellationToken)
                    .ConfigureAwait(false);
            if (result.Status is not (
                ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted))
            {
                return false;
            }

            _applied[runtimeKey] = new(identity, actionId, idempotencyKey);
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    public async ValueTask<bool> ReleaseAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        if (!_applied.Remove(runtimeKey, out RestraintRecord? record))
        {
            return false;
        }

        RuntimeProcessPriorityAction action = new(
            record.ActionId,
            record.Identity,
            ProcessPriorityClass.BelowNormal,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            record.ActionId,
            record.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<
                        RuntimeProcessPriorityState>(_journal)
                    .RecoverAsync(action, context, cancellationToken)
                    .ConfigureAwait(false);
            return result.Status
                is ActionRecoveryStatus.Restored
                or ActionRecoveryStatus.AlreadyRestored
                or ActionRecoveryStatus.NotRequired;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Skips a process already at or below BelowNormal. The action itself only
    /// allows lowering, but asking it to "lower" something already lower would
    /// still write a journal record for a change that changes nothing.
    /// </summary>
    private static bool IsWorthLowering(ProcessRuntimeKey runtimeKey)
    {
        try
        {
            using Process process =
                Process.GetProcessById(runtimeKey.ProcessId);
            return process.PriorityClass
                is not (ProcessPriorityClass.BelowNormal
                    or ProcessPriorityClass.Idle);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private async ValueTask<ProcessIdentity?> TryResolveAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessIdentity? identity = await _identityProvider
                .TryCaptureAsync(runtimeKey.ProcessId, cancellationToken)
                .ConfigureAwait(false);

            // Numer procesu mogl juz zmienic wlasciciela miedzy probka a
            // wykonaniem decyzji. Klucz niesie czas startu wlasnie po to.
            return identity is not null
                && identity.RuntimeKey == runtimeKey
                    ? identity
                    : null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is ArgumentException
            or InvalidOperationException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or Win32Exception
            or TimeoutException;

    private sealed record RestraintRecord(
        ProcessIdentity Identity,
        ActionId ActionId,
        IdempotencyKey IdempotencyKey);
}

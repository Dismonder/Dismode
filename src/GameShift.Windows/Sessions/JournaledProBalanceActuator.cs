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
using GameShift.Windows.NativeInterop;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

/// <summary>
/// Carries out ProBalance's decisions through the same transaction machinery
/// as every other change GameShift makes.
/// <para>
/// The in-memory actuator remembers what it changed and undoes it on stop,
/// which is enough while the process is alive. It is not enough if GameShift
/// dies mid-session: the priorities it lowered would stay lowered with nothing
/// left to record that it was us. Journal entries are written so that a
/// recovery pass has something to work from.
/// </para>
/// <para>
/// A journal entry alone is not recoverable: after a host crash nobody knows
/// which action ids belong to a restraint that was never released.
/// <c>LocalGameSessionOrchestrator.RecoverUserSessionAsync</c> replays only
/// what the session checkpoint lists. So every restraint is reported to the
/// <see cref="IRestraintLedger"/> the orchestrator attaches — before the
/// first change, with the ids of every action that may follow — and the
/// orchestrator writes it into the checkpoint and reverses it after a crash
/// through the same path as the planned applications. It matters more for
/// the I/O priority than for the mask: a mask shows in Task Manager and can
/// be undone by hand, an I/O priority shows in no standard tool at all.
/// </para>
/// </summary>
public sealed class JournaledProBalanceActuator :
    IProBalanceActuator,
    IRestraintLedgerAware
{
    private readonly IRecoveryJournal _journal;
    private readonly SessionId _sessionId;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly ProcessParentMapProvider _parentMapProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyList<uint> _backgroundCpuSetIds;

    /// <summary>
    /// Logical processors a restrained process is confined to. Zero means the
    /// machine did not qualify and only priority is lowered.
    /// <para>
    /// This is the part that actually moves frame times. Priority and CPU sets
    /// are both weighed by the scheduler and, against as many compute-bound
    /// threads as the machine has, both get outvoted; an affinity mask is a
    /// rule it cannot break. Measured on 7 Days To Die under full contention:
    /// priority alone moved p99 from 16,80 ms to 16,36 ms, the mask moved it
    /// to 10,65 ms.
    /// </para>
    /// </summary>
    private readonly ulong _backgroundAffinityMask;

    /// <summary>
    /// Czy procesom tla obnizac takze priorytet wejscia-wyjscia.
    /// <para>
    /// Maska odbiera rdzenie, ale nie odbiera dysku. Kopia zapasowa albo
    /// indeksowanie moze siedziec na dwoch rdzeniach i nadal zapychac kolejke
    /// odczytow, a gra czeka na swoje zasoby.
    /// </para>
    /// </summary>
    private readonly bool _lowerBackgroundIoPriority;
    private readonly Dictionary<ProcessRuntimeKey, RestraintRecord> _applied =
        [];

    /// <summary>
    /// Gdzie zglaszamy, co ograniczylismy, zeby przetrwalo awarie hosta.
    /// <para>
    /// Sam wpis w dzienniku nie wystarcza: po awarii nikt nie wie, ktore
    /// identyfikatory akcji naleza do ograniczenia, ktorego nigdy nie
    /// zdjeto. Punkt kontrolny sesji to jedyne miejsce, ktore awarie
    /// przezywa i jest odtwarzane przy nastepnym starcie.
    /// </para>
    /// </summary>
    private IRestraintLedger? _ledger;

    public JournaledProBalanceActuator(
        IRecoveryJournal journal,
        SessionId sessionId,
        IProcessIdentityProvider? identityProvider = null,
        TimeProvider? timeProvider = null,
        IReadOnlyList<uint>? backgroundCpuSetIds = null,
        ulong backgroundAffinityMask = 0,
        bool lowerBackgroundIoPriority = true)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _journal = journal;
        _sessionId = sessionId;
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _parentMapProvider = new ProcessParentMapProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _backgroundCpuSetIds = backgroundCpuSetIds ?? [];
        _backgroundAffinityMask = backgroundAffinityMask;
        _lowerBackgroundIoPriority = lowerBackgroundIoPriority;
    }

    public void AttachLedger(IRestraintLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
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
        PinnedAffinity? plannedPin = _backgroundAffinityMask == 0
            ? null
            : new(new(Guid.NewGuid()), IdempotencyKey.Create());
        LoweredIo? plannedIo = _lowerBackgroundIoPriority
            ? new(new(Guid.NewGuid()), IdempotencyKey.Create())
            : null;
        DateTimeOffset restrainedAtUtc = _timeProvider.GetUtcNow();

        // Meldunek do ksiegi PRZED pierwsza zmiana. Identyfikatory sa juz
        // znane, a odtwarzanie akcji, ktorej nigdy nie nalozono, jest
        // nieszkodliwe — dziennik nie ma dla niej wpisu o mutacji. Odwrotna
        // kolejnosc zostawiala okno: awaria hosta miedzy nalozeniem maski
        // a meldunkiem, i po ograniczeniu nie bylo sladu poza dziennikiem,
        // ktorego nikt nie potrafi dopasowac do procesu.
        await ReportRestraintAsync(
                identity,
                actionId,
                idempotencyKey,
                plannedPin,
                plannedIo,
                CancellationToken.None)
            .ConfigureAwait(false);

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
                // Nic nie nalozono, wiec meldunek sprzed chwili jest
                // bezprzedmiotowy.
                await ForgetRestraintAsync(runtimeKey, CancellationToken.None)
                    .ConfigureAwait(false);
                return false;
            }

            // Od tego miejsca proces JEST juz zmieniony: priorytet lezy na
            // nim i tylko nasz rekord pamieta, ze to my. Anulowanie nie moze
            // nas tu wyrzucic, bo wyjatek przeskoczylby zapis do _applied,
            // ReleaseAll nie mialoby czego zwalniac i proces zostalby
            // w cwiartce z obnizonym priorytetem na zawsze. Reszte robimy
            // wiec bez tokenu, a rekord powstaje w finally z tym, co realnie
            // zdazylo sie nalozyc.
            bool steered = false;
            PinnedAffinity? pinned = null;
            LoweredIo? loweredIo = null;
            try
            {
                // Odsuniecie od rdzeni gry. Idzie po ograniczeniu priorytetu,
                // bo tamto jest wazniejsze i nie chcemy, zeby nieudane
                // sterowanie zbiorami przeslonilo udane obnizenie priorytetu.
                steered = TrySteerAway(runtimeKey);
                pinned = await TryPinAsync(identity, plannedPin)
                    .ConfigureAwait(false);
                // Na koncu, bo z trzech ograniczen to najmniej sprawdzone.
                // Niepowodzenie nie moze przeslonic udanej maski.
                loweredIo = await TryLowerIoAsync(identity, plannedIo)
                    .ConfigureAwait(false);
            }
            finally
            {
                _applied[runtimeKey] = new(
                    identity,
                    actionId,
                    idempotencyKey,
                    steered,
                    pinned,
                    loweredIo,
                    restrainedAtUtc);
            }

            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // Wyjatek z transakcji priorytetu znaczy, ze nic nie nalozono
            // (kazdy pozniejszy krok lapie swoje wyjatki sam), a rekordu
            // w _applied nie ma. Meldunek sprzed chwili jest wtedy
            // bezprzedmiotowy.
            if (!_applied.ContainsKey(runtimeKey))
            {
                await ForgetRestraintAsync(runtimeKey, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return false;
        }
    }

    /// <summary>
    /// Zdejmuje ograniczenia z procesu.
    /// <para>
    /// Token jest przyjmowany dla zgodnosci z interfejsem, ale swiadomie
    /// nieuzywany: kazdy krok tej metody to przywracanie stanu, a przerwanie
    /// go w polowie zostawia proces ograniczony — czyli powoduje dokladnie
    /// to, czemu ta metoda ma zapobiegac.
    /// </para>
    /// </summary>
    public async ValueTask<bool> ReleaseAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Podgladamy, nie zdejmujemy. Usuniecie na wejsciu znaczyloby, ze
        // nieudane przywrocenie gubi jedyny slad po ograniczeniu, ktore
        // nadal obowiazuje. Rekord znika dopiero, gdy stan faktycznie wrocil.
        if (!_applied.TryGetValue(runtimeKey, out RestraintRecord? record))
        {
            return false;
        }

        if (record.Steered)
        {
            // Wyczyszczenie oddaje procesowi cala maszyne.
            _ = ProcessCpuSets.TryApply(runtimeKey.ProcessId, []);
        }

        // Cala ta sciezka idzie bez tokenu i jest to ta sama zasada, co przy
        // nakladaniu: zwalnianie przerwane w polowie zostawia proces
        // ograniczony, czyli dokladnie to, przed czym ma chronic. Praca jest
        // ograniczona co do rozmiaru, a wolajacy i tak czeka na worker.
        //
        // Kolejnosc odwrotna do nakladania: dysk, maska, priorytet. Potem
        // potomkowie — rozpoznajemy ich po ICH wartosciach, nie po rodzicu,
        // a maska, ktora maja dostac, to ta, ktora rodzic ma po przywroceniu.
        bool ioRestored = record.LoweredIo is not { } loweredIo
            || await RestoreIoAsync(record.Identity, loweredIo)
                .ConfigureAwait(false);
        bool pinRestored = record.Pinned is not { } pinned
            || await RestorePinAsync(record.Identity, pinned)
                .ConfigureAwait(false);

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

        bool priorityRestored;
        try
        {
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<
                        RuntimeProcessPriorityState>(_journal)
                    .RecoverAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            priorityRestored = IsRestored(result.Status);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            priorityRestored = false;
        }

        bool descendantsRestored = ReleaseDescendants(record);

        // Wykreslenie dopiero po rozliczeniu CALEGO pakietu. Rekord, ktory
        // zostaje, to jedyny slad po ograniczeniu, ktore moze wlasnie nie
        // zostalo zdjete — ksiega sesji ponowi probe przy jej zamknieciu.
        bool restored = ioRestored
            && pinRestored
            && priorityRestored
            && descendantsRestored;
        if (restored)
        {
            _ = _applied.Remove(runtimeKey);
            await ForgetRestraintAsync(runtimeKey, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return restored;
    }

    private static bool IsRestored(ActionRecoveryStatus status) =>
        status is ActionRecoveryStatus.Restored
            or ActionRecoveryStatus.AlreadyRestored
            or ActionRecoveryStatus.NotRequired;

    /// <summary>
    /// Releases what the restrained process's children inherited from it —
    /// the corner mask and the lowered I/O priority. Runs after the parent is
    /// restored, so the children are widened to what the parent has now.
    /// True when nothing that needed releasing was left behind.
    /// </summary>
    private bool ReleaseDescendants(RestraintRecord record)
    {
        if (record.Pinned is null && record.LoweredIo is null)
        {
            return true;
        }

        IReadOnlyDictionary<int, int> parents;
        try
        {
            parents = _parentMapProvider.Capture();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }

        InheritedRestraintSweep sweep = InheritedRestraintSweeper.Release(
            record.Identity.RuntimeKey.ProcessId,
            parents,
            record.RestrainedAtUtc,
            record.Pinned is null ? 0 : _backgroundAffinityMask,
            resetIoPriority: record.LoweredIo is not null,
            resetMemoryPriority: false);
        return sweep.Failed == 0;
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

    /// <summary>
    /// Steers a restrained process onto the processors the game is not using.
    /// <para>
    /// Lowering a priority only asks the scheduler to prefer the game when both
    /// want the same core. This moves the other process off those cores
    /// altogether, which on a machine with performance tiers or separate cache
    /// groups is the stronger of the two levers. It stays a preference, so a
    /// process that genuinely needs more still gets it — a hard mask here would
    /// risk stalling something the user never asked us to touch.
    /// </para>
    /// </summary>
    private bool TrySteerAway(ProcessRuntimeKey runtimeKey)
    {
        if (_backgroundCpuSetIds.Count == 0)
        {
            return false;
        }

        try
        {
            return ProcessCpuSets.TryApply(
                runtimeKey.ProcessId,
                _backgroundCpuSetIds);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Confines the process to the background corner of the machine. Journaled
    /// like every other change, because an affinity mask outlives GameShift:
    /// if we died here without a record, the process would stay squeezed into
    /// a quarter of the machine with nothing left to say it was us.
    /// </summary>
    private async ValueTask<PinnedAffinity?> TryPinAsync(
        ProcessIdentity identity,
        PinnedAffinity? planned)
    {
        if (planned is null)
        {
            return null;
        }

        ProcessAffinityAction action = new(
            planned.ActionId,
            identity,
            _backgroundAffinityMask,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            planned.ActionId,
            planned.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<ProcessAffinityState>(_journal)
                    .ExecuteAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            return result.Status is (
                ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted)
                ? planned
                : null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Zglasza ograniczenie do ksiegi sesji.
    /// <para>
    /// Niepowodzenie zapisu nie moze wywrocic samego ograniczenia — ono juz
    /// obowiazuje i jest w dzienniku. Traci wtedy tylko odtwarzalnosc po
    /// awarii, wiec lykamy wyjatek zamiast przewracac petle nadzorcy.
    /// </para>
    /// </summary>
    private async ValueTask ReportRestraintAsync(
        ProcessIdentity identity,
        ActionId priorityActionId,
        IdempotencyKey priorityIdempotencyKey,
        PinnedAffinity? pinned,
        LoweredIo? loweredIo,
        CancellationToken cancellationToken)
    {
        if (_ledger is not { } ledger)
        {
            return;
        }

        try
        {
            await ledger.RecordAsync(
                    new RestrainedProcessRecord(
                        identity,
                        Path.GetFileNameWithoutExtension(
                            identity.ExecutablePath),
                        priorityActionId,
                        priorityIdempotencyKey,
                        pinned?.ActionId,
                        pinned?.IdempotencyKey,
                        loweredIo?.ActionId,
                        loweredIo?.IdempotencyKey,
                        // Priorytet pamieci nalezy do orkiestratora, nie do
                        // tej petli.
                        null,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }
    }

    private async ValueTask ForgetRestraintAsync(
        ProcessRuntimeKey runtimeKey,
        CancellationToken cancellationToken)
    {
        if (_ledger is not { } ledger)
        {
            return;
        }

        try
        {
            await ledger.ForgetAsync(runtimeKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }
    }

    private async ValueTask<LoweredIo?> TryLowerIoAsync(
        ProcessIdentity identity,
        LoweredIo? planned)
    {
        if (planned is null)
        {
            return null;
        }

        ProcessIoPriorityAction action = new(
            planned.ActionId,
            identity,
            IoPriorityNativeMethods.IoPriorityVeryLow,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            planned.ActionId,
            planned.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<ProcessIoPriorityState>(
                        _journal)
                    .ExecuteAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            return result.Status is (
                ActionExecutionStatus.AppliedAndVerified
                or ActionExecutionStatus.AlreadyCompleted)
                ? planned
                : null;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    private async ValueTask<bool> RestoreIoAsync(
        ProcessIdentity identity,
        LoweredIo loweredIo)
    {
        ProcessIoPriorityAction action = new(
            loweredIo.ActionId,
            identity,
            IoPriorityNativeMethods.IoPriorityVeryLow,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            loweredIo.ActionId,
            loweredIo.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<ProcessIoPriorityState>(
                        _journal)
                    .RecoverAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            return IsRestored(result.Status);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private async ValueTask<bool> RestorePinAsync(
        ProcessIdentity identity,
        PinnedAffinity pinned)
    {
        // Odtwarzanie cofa do stanu z dziennika i nie potrzebuje maski.
        ProcessAffinityAction action = ProcessAffinityAction.ForRecovery(
            pinned.ActionId,
            identity,
            _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            pinned.ActionId,
            pinned.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<ProcessAffinityState>(
                        _journal)
                    .RecoverAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            return IsRestored(result.Status);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private sealed record PinnedAffinity(
        ActionId ActionId,
        IdempotencyKey IdempotencyKey);

    private sealed record LoweredIo(
        ActionId ActionId,
        IdempotencyKey IdempotencyKey);

    private sealed record RestraintRecord(
        ProcessIdentity Identity,
        ActionId ActionId,
        IdempotencyKey IdempotencyKey,
        bool Steered,
        PinnedAffinity? Pinned,
        LoweredIo? LoweredIo,
        DateTimeOffset RestrainedAtUtc);
}

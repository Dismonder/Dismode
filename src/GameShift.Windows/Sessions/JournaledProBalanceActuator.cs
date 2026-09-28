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

    /// <summary>
    /// Czy ograniczanemu procesowi wlaczac EcoQoS (preset agresywny).
    /// </summary>
    private readonly bool _applyEcoQos;
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
        bool lowerBackgroundIoPriority = true,
        bool applyEcoQos = false)
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
        _applyEcoQos = applyEcoQos;
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
        // odtwarzanie z ksiegi sesji.
        IdempotencyKey idempotencyKey = IdempotencyKey.Create();
        PinnedAffinity? plannedPin = _backgroundAffinityMask == 0
            ? null
            : new(new(Guid.NewGuid()), IdempotencyKey.Create());
        LoweredIo? plannedIo = _lowerBackgroundIoPriority
            ? new(new(Guid.NewGuid()), IdempotencyKey.Create())
            : null;
        ThrottledEcoQos? plannedEcoQos = _applyEcoQos
            ? new(new(Guid.NewGuid()), IdempotencyKey.Create())
            : null;
        bool plannedSteer = _backgroundCpuSetIds.Count > 0;
        DateTimeOffset restrainedAtUtc = _timeProvider.GetUtcNow();

        // Meldunek do ksiegi PRZED pierwsza zmiana i tylko potwierdzony.
        // Identyfikatory sa juz znane, a odtwarzanie akcji, ktorej nigdy nie
        // nalozono, jest nieszkodliwe — dziennik nie ma dla niej wpisu
        // o mutacji. Bez potwierdzonego zapisu nie ma ograniczenia:
        // ograniczenie jest opcjonalne, odtwarzalnosc po awarii nie.
        // Wczesniej nieudany zapis byl polykany i proces byl zmieniany
        // bez sladu w punkcie kontrolnym.
        // Zbiory CPU meldujemy dopiero po nalozeniu (drugi meldunek nizej):
        // nie sa dziennikowane, wiec odtwarzanie po awarii czysciloby je na
        // podstawie samego planu — takze procesowi, ktory mial wlasne zbiory
        // i ktorego host nie zdazyl ruszyc. Awaria miedzy nalozeniem a drugim
        // meldunkiem zostawia preferencje, nie ograniczenie.
        if (!await ReportRestraintAsync(
                identity,
                actionId,
                idempotencyKey,
                plannedPin,
                plannedIo,
                plannedEcoQos,
                steeredCpuSets: false,
                restrainedAtUtc)
            .ConfigureAwait(false))
        {
            // Ksiega po swojej stronie cofa nieudany wpis; to wywolanie jest
            // pasem bezpieczenstwa, gdyby implementacja ksiegi tego nie
            // robila — wpis w pamieci bez sladu na dysku bylby widmem.
            await ForgetRestraintAsync(runtimeKey).ConfigureAwait(false);
            return false;
        }

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

        ActionExecutionResult result;
        try
        {
            result = await new TransactionCoordinator<
                    RuntimeProcessPriorityState>(_journal)
                .ExecuteAsync(action, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            // Nie wiemy, czy priorytet juz lezy na procesie: wyjatek mogl
            // przyjsc po ApplyAsync, a przed wpisem ActionApplied, i wtedy
            // transakcja niczego nie cofa. Rekord zostaje — w ksiedze
            // i tutaj — a zwolnienie pojdzie przez odtwarzanie, ktore
            // rozstrzyga to z dziennika: akcja nigdy nierozpoczeta wraca
            // jako NotRequired albo MissingPreparation, rozpoczeta zostaje
            // cofnieta. Zapomnienie meldunku w tym miejscu kasowalo jedyny
            // slad po zmianie, ktora mogla zajsc.
            Remember(
                runtimeKey,
                identity,
                actionId,
                idempotencyKey,
                plannedPin,
                plannedIo,
                plannedEcoQos,
                restrainedAtUtc);
            return true;
        }

        switch (result.Status)
        {
            case ActionExecutionStatus.Blocked:
                // Walidacja odmowila przed jakakolwiek zmiana; dziennik ma
                // tylko ActionPrepared i ActionBlocked. Meldunek sprzed
                // chwili jest bezprzedmiotowy.
                await ForgetRestraintAsync(runtimeKey).ConfigureAwait(false);
                return false;
            case ActionExecutionStatus.AppliedAndVerified:
            case ActionExecutionStatus.AlreadyCompleted:
                break;
            default:
                // RecoveryRequired albo VerificationFailed: proces mogl
                // zostac zmieniony. Kolejnych dzwigni nie dokladamy do
                // procesu w niejasnym stanie, ale rekord zostaje, zeby
                // zwolnienie i odtwarzanie mialy co cofac.
                Remember(
                    runtimeKey,
                    identity,
                    actionId,
                    idempotencyKey,
                    plannedPin,
                    plannedIo,
                    plannedEcoQos,
                    restrainedAtUtc);
                return true;
        }

        // Od tego miejsca proces JEST juz zmieniony: priorytet lezy na nim
        // i tylko nasz rekord pamieta, ze to my. Anulowanie nie moze nas tu
        // wyrzucic, bo wyjatek przeskoczylby zapis do _applied, ReleaseAll
        // nie mialoby czego zwalniac i proces zostalby w cwiartce
        // z obnizonym priorytetem na zawsze. Reszte robimy wiec bez tokenu,
        // a rekord powstaje w finally z tym, co realnie zdazylo sie nalozyc
        // — albo moglo sie nalozyc, bo niepewnosc tez trzeba pamietac.
        bool steered = false;
        PinnedAffinity? pinned = null;
        ThrottledEcoQos? ecoQos = null;
        LoweredIo? loweredIo = null;
        try
        {
            // Odsuniecie od rdzeni gry. Idzie po ograniczeniu priorytetu,
            // bo tamto jest wazniejsze i nie chcemy, zeby nieudane
            // sterowanie zbiorami przeslonilo udane obnizenie priorytetu.
            steered = plannedSteer && TrySteerAway(runtimeKey);
            pinned = await TryPinAsync(identity, plannedPin)
                .ConfigureAwait(false);
            ecoQos = await TryApplyEcoQosAsync(identity, plannedEcoQos)
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
                restrainedAtUtc,
                ecoQos);
        }

        // Drugi meldunek, z tym, co realnie sie nalozylo. Pierwszy szedl
        // z planem, bo musial wyprzedzic mutacje; ten prostuje ksiege, zeby
        // odtwarzanie po awarii nie przegladalo potomkow po dzwigniach,
        // ktorych nie bylo. Best-effort: gdy sie nie powiedzie, w ksiedze
        // zostaje plan, czyli wersja ostrozniejsza.
        _ = await ReportRestraintAsync(
                identity,
                actionId,
                idempotencyKey,
                pinned,
                loweredIo,
                ecoQos,
                steered,
                restrainedAtUtc)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Records a restraint whose priority transaction ended in doubt. The
    /// planned ids stay in the record because the ledger already carries
    /// them; releasing an action that never started is a no-op through the
    /// journal, so keeping them costs nothing and losing them could leave
    /// a real change unreversed.
    /// </summary>
    private void Remember(
        ProcessRuntimeKey runtimeKey,
        ProcessIdentity identity,
        ActionId actionId,
        IdempotencyKey idempotencyKey,
        PinnedAffinity? plannedPin,
        LoweredIo? plannedIo,
        ThrottledEcoQos? plannedEcoQos,
        DateTimeOffset restrainedAtUtc) =>
        _applied[runtimeKey] = new(
            identity,
            actionId,
            idempotencyKey,
            Steered: false,
            plannedPin,
            plannedIo,
            restrainedAtUtc,
            plannedEcoQos);

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

        // Zbiory czyscimy dopiero po sprawdzeniu, ze za numerem stoi nadal
        // ten sam proces. Zwolnienie „zniknal z inwentaryzacji" przychodzi
        // wlasnie wtedy, gdy numer mogl juz zmienic wlasciciela, a czyszczenie
        // po samym numerze trafialoby w obcy proces.
        bool setsCleared = !record.Steered
            || await TryClearCpuSetsAsync(record.Identity).ConfigureAwait(false);

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
        bool ecoQosRestored = record.EcoQos is not { } ecoQos
            || await RestoreEcoQosAsync(record.Identity, ecoQos)
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
        bool restored = setsCleared
            && ioRestored
            && ecoQosRestored
            && pinRestored
            && priorityRestored
            && descendantsRestored;
        if (restored)
        {
            _ = _applied.Remove(runtimeKey);
            await ForgetRestraintAsync(runtimeKey).ConfigureAwait(false);
        }

        return restored;
    }

    /// <summary>
    /// Statuses after which nothing of ours is left on the process.
    /// MissingPreparation belongs here: a planned action whose transaction
    /// never wrote a preparation record never touched the process either.
    /// </summary>
    private static bool IsRestored(ActionRecoveryStatus status) =>
        status is ActionRecoveryStatus.Restored
            or ActionRecoveryStatus.AlreadyRestored
            or ActionRecoveryStatus.NotRequired
            or ActionRecoveryStatus.MissingPreparation;

    /// <summary>
    /// Clears the default CPU sets of a restrained process, but only when the
    /// process behind the id is still the one that was restrained. A process
    /// that has exited has nothing to clear; its number may already belong to
    /// something else.
    /// </summary>
    private async ValueTask<bool> TryClearCpuSetsAsync(ProcessIdentity identity)
    {
        try
        {
            bool sameProcess = await _identityProvider
                .MatchesRuntimeIdentityAsync(identity, CancellationToken.None)
                .ConfigureAwait(false);
            return !sameProcess
                || ProcessCpuSets.TryApply(identity.RuntimeKey.ProcessId, []);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Releases what the restrained process's children inherited from it —
    /// the corner mask, the lowered I/O priority and the BelowNormal class,
    /// which Windows hands to every child of a BelowNormal parent. Runs after
    /// the parent is restored, so the children are widened to what the parent
    /// has now. True when nothing that needed releasing was left behind.
    /// </summary>
    private bool ReleaseDescendants(RestraintRecord record)
    {
        InheritedRestraintSweep sweep = InheritedRestraintSweeper.Release(
            record.Identity.RuntimeKey.ProcessId,
            record.Identity.RuntimeKey.StartedAtUtc,
            CaptureParentsOrNull,
            record.RestrainedAtUtc,
            record.Pinned is null ? 0 : _backgroundAffinityMask,
            resetIoPriority: record.LoweredIo is not null,
            resetMemoryPriority: false,
            resetPriorityClass: true);
        return sweep.Complete;
    }

    private IReadOnlyDictionary<int, int>? CaptureParentsOrNull()
    {
        try
        {
            return _parentMapProvider.Capture();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// A process at Normal or BelowNormal qualifies. BelowNormal used to be
    /// skipped as "already lowered", which also skipped the mask — the one
    /// lever that moves frame times — for every hog that lowers itself, and
    /// browsers' renderers and updaters routinely do. The priority action
    /// treats BelowNormal to BelowNormal as a no-op. Idle and anything above
    /// Normal stay out of reach: the action would refuse them anyway.
    /// </summary>
    private static bool IsWorthLowering(ProcessRuntimeKey runtimeKey)
    {
        try
        {
            using Process process =
                Process.GetProcessById(runtimeKey.ProcessId);
            return process.PriorityClass
                is ProcessPriorityClass.Normal
                    or ProcessPriorityClass.BelowNormal;
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
            // Tylko odmowa walidacji znaczy „nic nie nalozono". Kazdy inny
            // wynik — takze VerificationFailed i wyjatek po ApplyAsync —
            // zostawia identyfikatory w rekordzie, bo zmiana mogla zajsc,
            // a odtwarzanie akcji nierozpoczetej jest nieszkodliwe.
            return result.Status == ActionExecutionStatus.Blocked
                ? null
                : planned;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return planned;
        }
    }

    /// <summary>
    /// Zglasza ograniczenie do ksiegi sesji, zanim cokolwiek zostanie
    /// zmienione. Zwraca false, gdy zapis sie nie powiodl — wtedy wolajacy
    /// nie zmienia procesu. Bez ksiegi (testy, aktuator z fabryki) zawsze
    /// true.
    /// <para>
    /// Wczesniej nieudany zapis byl polykany z uzasadnieniem, ze ograniczenie
    /// „juz obowiazuje". Po przeniesieniu meldunku przed mutacje to
    /// uzasadnienie przestalo byc prawdziwe, a skutek zostal: proces
    /// zmieniony bez sladu w punkcie kontrolnym i po awarii hosta nie do
    /// odtworzenia.
    /// </para>
    /// </summary>
    private async ValueTask<bool> ReportRestraintAsync(
        ProcessIdentity identity,
        ActionId priorityActionId,
        IdempotencyKey priorityIdempotencyKey,
        PinnedAffinity? pinned,
        LoweredIo? loweredIo,
        ThrottledEcoQos? ecoQos,
        bool steeredCpuSets,
        DateTimeOffset restrainedAtUtc)
    {
        if (_ledger is not { } ledger)
        {
            return true;
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
                        null,
                        restrainedAtUtc,
                        steeredCpuSets,
                        ecoQos?.ActionId,
                        ecoQos?.IdempotencyKey),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return false;
        }
    }

    private async ValueTask ForgetRestraintAsync(ProcessRuntimeKey runtimeKey)
    {
        if (_ledger is not { } ledger)
        {
            return;
        }

        try
        {
            await ledger.ForgetAsync(runtimeKey, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
        }
    }

    private async ValueTask<ThrottledEcoQos?> TryApplyEcoQosAsync(
        ProcessIdentity identity,
        ThrottledEcoQos? planned)
    {
        if (planned is null)
        {
            return null;
        }

        RuntimeProcessEcoQosAction action = new(
            planned.ActionId,
            identity,
            identityProvider: _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            planned.ActionId,
            planned.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionExecutionResult result =
                await new TransactionCoordinator<RuntimeProcessEcoQosState>(
                        _journal)
                    .ExecuteAsync(action, context, CancellationToken.None)
                    .ConfigureAwait(false);
            // Jak przy wejsciu-wyjsciu: tylko odmowa walidacji znaczy, ze nic
            // nie nalozono. Reszta zostawia identyfikatory w rekordzie.
            return result.Status == ActionExecutionStatus.Blocked
                ? null
                : planned;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return planned;
        }
    }

    private async ValueTask<bool> RestoreEcoQosAsync(
        ProcessIdentity identity,
        ThrottledEcoQos ecoQos)
    {
        RuntimeProcessEcoQosAction action = new(
            ecoQos.ActionId,
            identity,
            identityProvider: _identityProvider);
        ActionExecutionContext context = new(
            _sessionId,
            ecoQos.ActionId,
            ecoQos.IdempotencyKey,
            _timeProvider.GetUtcNow());

        try
        {
            ActionRecoveryResult result =
                await new ActionRecoveryCoordinator<RuntimeProcessEcoQosState>(
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
            // Jak przy masce: tylko odmowa walidacji znaczy, ze nic nie
            // nalozono. Reszta zostawia identyfikatory w rekordzie.
            return result.Status == ActionExecutionStatus.Blocked
                ? null
                : planned;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return planned;
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

    private sealed record ThrottledEcoQos(
        ActionId ActionId,
        IdempotencyKey IdempotencyKey);

    private sealed record RestraintRecord(
        ProcessIdentity Identity,
        ActionId ActionId,
        IdempotencyKey IdempotencyKey,
        bool Steered,
        PinnedAffinity? Pinned,
        LoweredIo? LoweredIo,
        DateTimeOffset RestrainedAtUtc,
        ThrottledEcoQos? EcoQos = null);
}

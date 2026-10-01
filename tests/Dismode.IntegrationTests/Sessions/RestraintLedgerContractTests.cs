using System.Diagnostics;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Journal;
using Dismode.Data.Journal;
using Dismode.Windows.NativeInterop;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Sessions;

/// <summary>
/// The contract between the reactive actuator and the session ledger, under
/// the two failures that used to lose a restraint: the ledger refusing the
/// record, and the journal failing after the process was already changed.
/// <para>
/// Both are exercised against a real process with faults injected at the
/// seam, because both bugs were in the ordering of real calls: a swallowed
/// ledger exception followed by a real mutation, and a forgotten ledger
/// record after a transaction that had already applied.
/// </para>
/// </summary>
[TestClass]
public sealed class RestraintLedgerContractTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-ksiega-" + Guid.NewGuid().ToString("N"));
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
    [Timeout(60_000)]
    public async Task FailedLedgerWriteLeavesTheProcessUntouched()
    {
        // Ograniczenie jest opcjonalne, odtwarzalnosc po awarii nie. Jesli
        // punkt kontrolny nie przyjal meldunku, proces ma zostac dokladnie
        // taki, jaki byl — lacznie z tym, ze dziennik nie ma nawet
        // przygotowania transakcji, bo meldunek idzie przed nia.
        _target = StartSleeper();
        ProcessRuntimeKey key = new(
            _target.Id,
            _target.StartTime.ToUniversalTime());
        nint originalAffinity = _target.ProcessorAffinity;
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundAffinityMask: 0xF,
            lowerBackgroundIoPriority: true);
        actuator.AttachLedger(new FailingLedger());

        bool restrained = await actuator.RestrainAsync(
            key,
            CancellationToken.None);

        Assert.IsFalse(
            restrained,
            "Bez potwierdzonego meldunku w ksiedze nie wolno zmieniac procesu.");
        _target.Refresh();
        Assert.AreEqual(ProcessPriorityClass.Normal, _target.PriorityClass);
        Assert.AreEqual(originalAffinity, _target.ProcessorAffinity);
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityNormal,
            ReadIoPriority(_target));
        Assert.IsEmpty(
            await journal.ReadAllAsync(CancellationToken.None),
            "Meldunek idzie przed transakcja, wiec po odmowie dziennik ma "
                + "byc pusty.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task JournalFailureAfterTheChangeKeepsTheRecordAndReleaseUndoesIt()
    {
        // Zapis ActionApplied pada juz PO tym, jak priorytet lezy na
        // procesie. Transakcja tego nie cofa. Aktuator nie moze wtedy
        // zapomniec meldunku ani zgubic rekordu — ma go zachowac, a
        // zwolnienie ma przejsc przez odtwarzanie, ktore z dziennika
        // (ActionApplying jest zapisane) wie, ze zmiana mogla zajsc.
        _target = StartSleeper();
        ProcessRuntimeKey key = new(
            _target.Id,
            _target.StartTime.ToUniversalTime());
        using AppendOnlyRecoveryJournal inner =
            new(Path.Combine(_directory, "recovery.jsonl"));
        FaultingJournal journal = new(
            inner,
            draft => draft.EventKind == JournalEventKind.ActionApplied);
        RecordingLedger ledger = new();
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundAffinityMask: 0,
            lowerBackgroundIoPriority: false);
        actuator.AttachLedger(ledger);

        bool restrained = await actuator.RestrainAsync(
            key,
            CancellationToken.None);

        Assert.AreEqual(
            1,
            journal.FailedAppends,
            "Warunek testu: dziennik mial odmowic dokladnie raz, przy "
                + "ActionApplied.");
        _target.Refresh();
        Assert.AreEqual(
            ProcessPriorityClass.BelowNormal,
            _target.PriorityClass,
            "Warunek testu: zmiana miala zajsc, zanim dziennik odmowil.");
        Assert.IsTrue(
            restrained,
            "Po zmianie procesu wynik ma mowic „ograniczony”, zeby silnik "
                + "kiedys wydal zwolnienie.");
        Assert.HasCount(1, ledger.Recorded);
        Assert.IsEmpty(
            ledger.Forgotten,
            "Meldunek zostal zapomniany, choc proces jest zmieniony. Po "
                + "awarii hosta nikt by go nie odtworzyl.");

        bool released = await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.IsTrue(released, "Zwolnienie ma przejsc przez odtwarzanie.");
        _target.Refresh();
        Assert.AreEqual(ProcessPriorityClass.Normal, _target.PriorityClass);
        Assert.HasCount(1, ledger.Forgotten);
    }

    private sealed class FailingLedger : IRestraintLedger
    {
        public ValueTask RecordAsync(
            RestrainedProcessRecord record,
            CancellationToken cancellationToken) =>
            throw new IOException("Symulowana awaria zapisu punktu kontrolnego.");

        public ValueTask ForgetAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RecordingLedger : IRestraintLedger
    {
        public List<RestrainedProcessRecord> Recorded { get; } = [];

        public List<ProcessRuntimeKey> Forgotten { get; } = [];

        public ValueTask RecordAsync(
            RestrainedProcessRecord record,
            CancellationToken cancellationToken)
        {
            Recorded.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask ForgetAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            Forgotten.Add(runtimeKey);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A journal that refuses one append matching a predicate, then behaves.
    /// </summary>
    private sealed class FaultingJournal(
        IRecoveryJournal inner,
        Func<RecoveryJournalDraft, bool> shouldFailOnce) : IRecoveryJournal
    {
        private bool _failed;

        public int FailedAppends { get; private set; }

        public ValueTask<RecoveryJournalEntry> AppendDurableAsync(
            RecoveryJournalDraft draft,
            CancellationToken cancellationToken)
        {
            if (!_failed && shouldFailOnce(draft))
            {
                _failed = true;
                FailedAppends++;
                throw new IOException("Symulowana awaria zapisu dziennika.");
            }

            return inner.AppendDurableAsync(draft, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllAsync(
            CancellationToken cancellationToken) =>
            inner.ReadAllAsync(cancellationToken);
    }

    private static uint ReadIoPriority(Process process)
    {
        int status = IoPriorityNativeMethods.NtQueryInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            out uint priority,
            sizeof(uint),
            out _);
        Assert.AreEqual(
            IoPriorityNativeMethods.StatusSuccess,
            status,
            $"Odczyt priorytetu I/O zwrocil NTSTATUS 0x{status:X8}.");
        return priority;
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
}

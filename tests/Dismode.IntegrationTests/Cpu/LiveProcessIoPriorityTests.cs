using System.Diagnostics;
using Dismode.Contracts.Protocol;
using Dismode.Core.Actions;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Core.Recovery;
using Dismode.Core.Transactions;
using Dismode.Data.Journal;
using Dismode.Windows.NativeInterop;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Cpu;

/// <summary>
/// Priorytet wejscia-wyjscia na prawdziwym procesie.
/// <para>
/// Maska powinowactwa odbiera procesowi tla rdzenie, ale nie odbiera mu
/// dysku, wiec kopia zapasowa albo indeksowanie moze siedziec na dwoch
/// rdzeniach i dalej zapychac kolejke odczytow. Ten test sprawdza sam
/// mechanizm: czy zapis przechodzi, czy odczyt go potwierdza i czy droga
/// przez journal przywraca stan sprzed zmiany.
/// </para>
/// <para>
/// Czego ten test NIE sprawdza: czy to poprawia czas klatki w grze. To
/// wymaga zywej sesji i sparowanego pomiaru, a tu mierzymy wylacznie, ze
/// system przyjmuje i cofa zmiane.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveProcessIoPriorityTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-iopriority-" + Guid.NewGuid().ToString("N"));
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
    public async Task IoPriorityIsLoweredVerifiedAndPutBack()
    {
        _target = StartIdleProcess();
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        uint original = ReadIoPriority(_target);
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityNormal,
            original,
            "Swiezy proces powinien miec zwykly priorytet wejscia-wyjscia.");

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        ActionId actionId = new(Guid.NewGuid());
        ProcessIoPriorityAction action = new(
            actionId,
            identity,
            IoPriorityNativeMethods.IoPriorityVeryLow);
        ActionExecutionContext context = new(
            new SessionId(Guid.NewGuid()),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);
        TransactionCoordinator<ProcessIoPriorityState> coordinator =
            new(journal);

        ActionExecutionResult applied = await coordinator.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            applied.Status,
            applied.Details);
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityVeryLow,
            ReadIoPriority(_target),
            "Priorytet wejscia-wyjscia nie zostal obnizony.");

        // Ta sama droga, co po awarii aplikacji w trakcie sesji.
        ActionRecoveryResult restored =
            await new ActionRecoveryCoordinator<ProcessIoPriorityState>(journal)
                .RecoverAsync(action, context, CancellationToken.None);

        Assert.AreEqual(
            ActionRecoveryStatus.Restored,
            restored.Status,
            restored.Details);
        Assert.AreEqual(
            original,
            ReadIoPriority(_target),
            "Pierwotny priorytet wejscia-wyjscia nie zostal przywrocony.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RaisingIoPriorityIsRefused()
    {
        _target = StartIdleProcess();
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        // Podnoszenie priorytetu procesowi tla nie ma uzasadnienia i moglo by
        // zaszkodzic grze, wiec akcja nie pozwala sie nawet zbudowac.
        ArgumentOutOfRangeException blad =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                _ = new ProcessIoPriorityAction(
                    new ActionId(Guid.NewGuid()),
                    identity,
                    IoPriorityNativeMethods.IoPriorityNormal + 1));

        StringAssert.Contains(blad.Message, "obniżania");
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
            $"Odczyt priorytetu zwrocil NTSTATUS 0x{status:X8}.");
        return priority;
    }

    private static async Task<ProcessIdentity> CaptureIdentityAsync(
        Process process)
    {
        ProcessIdentity? identity = await new ProcessIdentityProvider()
            .TryCaptureAsync(process.Id, CancellationToken.None);
        Assert.IsNotNull(identity, "Nie udalo sie odczytac tozsamosci procesu.");
        return identity;
    }

    private static Process StartIdleProcess()
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
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 180");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        Thread.Sleep(1200);
        process.Refresh();
        return process;
    }
}

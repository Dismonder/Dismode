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
/// Priorytet GPU na prawdziwym procesie: czy system przyjmuje zapis, czy
/// odczyt go potwierdza i czy droga przez journal przywraca stan sprzed
/// zmiany. NIE sprawdza wplywu na czas klatki — to wymaga zywej sesji.
/// </summary>
[TestClass]
public sealed class LiveProcessGpuPriorityTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-gpupriority-" + Guid.NewGuid().ToString("N"));
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
    public async Task GpuPriorityIsLoweredVerifiedAndPutBack()
    {
        _target = StartIdleProcess();
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        int original = ReadGpuPriority(_target);
        Assert.AreEqual(
            GpuPriorityNativeMethods.Normal,
            original,
            "Swiezy proces powinien miec zwykla klase GPU.");

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        ActionId actionId = new(Guid.NewGuid());
        ProcessGpuPriorityAction action = new(actionId, identity);
        ActionExecutionContext context = new(
            new SessionId(Guid.NewGuid()),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

        ActionExecutionResult applied =
            await new TransactionCoordinator<ProcessGpuPriorityState>(journal)
                .ExecuteAsync(action, context, CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            applied.Status,
            applied.Details);
        Assert.AreEqual(
            GpuPriorityNativeMethods.BelowNormal,
            ReadGpuPriority(_target),
            "Klasa GPU nie zostala obnizona.");

        ActionRecoveryResult restored =
            await new ActionRecoveryCoordinator<ProcessGpuPriorityState>(
                    journal)
                .RecoverAsync(action, context, CancellationToken.None);

        Assert.AreEqual(
            ActionRecoveryStatus.Restored,
            restored.Status,
            restored.Details);
        Assert.AreEqual(
            original,
            ReadGpuPriority(_target),
            "Pierwotna klasa GPU nie zostala przywrocona.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task RaisingGpuPriorityIsRefused()
    {
        _target = StartIdleProcess();
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        ArgumentOutOfRangeException blad =
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                _ = new ProcessGpuPriorityAction(
                    new ActionId(Guid.NewGuid()),
                    identity,
                    GpuPriorityNativeMethods.Normal + 1));

        StringAssert.Contains(blad.Message, "obniżania");
    }

    private static int ReadGpuPriority(Process process)
    {
        int status = GpuPriorityNativeMethods
            .D3DKMTGetProcessSchedulingPriorityClass(
                process.Handle,
                out int priorityClass);
        Assert.AreEqual(
            GpuPriorityNativeMethods.StatusSuccess,
            status,
            $"Odczyt klasy GPU zwrocil NTSTATUS 0x{status:X8}.");
        return priorityClass;
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

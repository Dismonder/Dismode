using System.Diagnostics;
using GameShift.Contracts.Protocol;
using GameShift.Core.Actions;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Core.Recovery;
using GameShift.Core.Transactions;
using GameShift.Data.Journal;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// The affinity action against a real process. The policy declines on a
/// uniform CPU, so the mask is supplied directly here — otherwise this whole
/// path would stay unexercised on any machine without performance and
/// efficiency cores, which is most of them.
/// </summary>
[TestClass]
public sealed class LiveProcessAffinityTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "gameshift-affinity-" + Guid.NewGuid().ToString("N"));
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
    public async Task AffinityIsAppliedVerifiedAndPutBack()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Inconclusive("Potrzebne co najmniej dwa procesory logiczne.");
            return;
        }

        _target = StartIdleProcess();
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        _target.Refresh();
        ulong original = (ulong)_target.ProcessorAffinity.ToInt64();

        // Polowa tego, co proces ma teraz. Akcja wolno tylko zwezac, wiec
        // maska musi byc podzbiorem obecnej.
        ulong narrowed = KeepLowestHalf(original);
        Assert.AreNotEqual(original, narrowed);
        Assert.AreEqual(narrowed, narrowed & original);

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        ActionId actionId = new(Guid.NewGuid());
        ProcessAffinityAction action = new(actionId, identity, narrowed);
        ActionExecutionContext context = new(
            new SessionId(Guid.NewGuid()),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);
        TransactionCoordinator<ProcessAffinityState> coordinator = new(journal);

        ActionExecutionResult applied = await coordinator.ExecuteAsync(
            action,
            context,
            CancellationToken.None);

        Assert.AreEqual(
            ActionExecutionStatus.AppliedAndVerified,
            applied.Status,
            applied.Details);
        _target.Refresh();
        Assert.AreEqual(
            narrowed,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Maska nie zostala zastosowana.");

        // Odtworzenie z journala, ta sama droga co po awarii aplikacji.
        ActionRecoveryResult restored =
            await new ActionRecoveryCoordinator<ProcessAffinityState>(journal)
                .RecoverAsync(action, context, CancellationToken.None);

        Assert.AreEqual(
            ActionRecoveryStatus.Restored,
            restored.Status,
            restored.Details);
        _target.Refresh();
        Assert.AreEqual(
            original,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Pierwotna maska nie zostala przywrocona.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task WideningTheMaskIsRefused()
    {
        if (Environment.ProcessorCount < 2)
        {
            Assert.Inconclusive("Potrzebne co najmniej dwa procesory logiczne.");
            return;
        }

        // GameShift zweza albo nic. Rozszerzanie dawaloby procesowi rdzenie,
        // ktore wlasciciel mu swiadomie odebral.
        _target = StartIdleProcess();
        _target.ProcessorAffinity = (nint)(long)KeepLowestHalf(
            (ulong)_target.ProcessorAffinity.ToInt64());
        ProcessIdentity identity = await CaptureIdentityAsync(_target);

        ulong everything = Environment.ProcessorCount >= 64
            ? ulong.MaxValue
            : (1UL << Environment.ProcessorCount) - 1;
        ActionId actionId = new(Guid.NewGuid());
        ProcessAffinityAction action = new(actionId, identity, everything);
        ActionExecutionContext context = new(
            new SessionId(Guid.NewGuid()),
            actionId,
            IdempotencyKey.Create(),
            DateTimeOffset.UtcNow);

        ActionValidationResult validation = await action.ValidateAsync(
            context,
            await action.PrepareAsync(context, CancellationToken.None),
            CancellationToken.None);

        Assert.IsFalse(validation.IsValid, validation.BlockingReason);
    }

    private static ulong KeepLowestHalf(ulong mask)
    {
        int keep = Math.Max(1, System.Numerics.BitOperations.PopCount(mask) / 2);
        ulong result = 0;
        int taken = 0;
        for (int bit = 0; bit < 64 && taken < keep; bit++)
        {
            ulong candidate = 1UL << bit;
            if ((mask & candidate) == 0)
            {
                continue;
            }

            result |= candidate;
            taken++;
        }

        return result;
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

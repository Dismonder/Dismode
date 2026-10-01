using System.Diagnostics;
using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Domain.Processes;
using Dismode.Data.Journal;
using Dismode.Windows.NativeInterop;
using Dismode.Windows.Processes;
using Dismode.Windows.Sessions;

namespace Dismode.IntegrationTests.Processes;

/// <summary>
/// The descendant sweep's value rules against real processes: what it must
/// not touch, and what it must hand back exactly.
/// </summary>
[TestClass]
public sealed class InheritedRestraintSweeperLiveTests
{
    private const ulong Corner = 0xF;
    private readonly List<Process> _processes = [];
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-sweeper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (Process process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _processes.Clear();
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
    public async Task OrphanCarryingOnlyItsOwnLowIoPriorityIsLeftAlone()
    {
        RequireCorner();

        // Sierota: proces, ktorego rodzic juz nie zyje. Nie ma udowodnionego
        // pokrewienstwa z ograniczonym korzeniem, wiec liczy sie tylko odcisk.
        // Sam priorytet I/O VeryLow to wartosc, ktora procesy wybieraja tez
        // same — i tak wlasnie ten proces ja wybral.
        Process orphan = await StartOrphanAsync();
        SetIoPriority(orphan, IoPriorityNativeMethods.IoPriorityVeryLow);
        Process root = StartSleeper();
        DateTimeOffset rootStartedAtUtc = new(
            root.StartTime.ToUniversalTime(),
            TimeSpan.Zero);
        ProcessParentMapProvider parents = new();

        InheritedRestraintSweep first = InheritedRestraintSweeper.Release(
            root.Id,
            rootStartedAtUtc,
            parents.Capture,
            notBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            cornerMask: Corner,
            resetIoPriority: true,
            resetMemoryPriority: false,
            resetPriorityClass: true);

        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityVeryLow,
            ReadIoPriority(orphan),
            "Sierota bez maski cwiartki nie niesie pelnego odcisku "
                + "ograniczenia. Jej wlasny wybor zostal cofniety.");
        Assert.AreEqual(0, first.Failed);

        // Ten sam proces z pelnym odciskiem — maska cwiartki i VeryLow naraz
        // — to juz nie wybor, ktorego ktos dokonuje sam. Tego sweep ma
        // dosiegnac, bo wlasnie tak wyglada wnuk za zakonczonym posrednikiem.
        orphan.ProcessorAffinity = (nint)(long)Corner;

        InheritedRestraintSweep second = InheritedRestraintSweeper.Release(
            root.Id,
            rootStartedAtUtc,
            parents.Capture,
            notBeforeUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            cornerMask: Corner,
            resetIoPriority: true,
            resetMemoryPriority: false,
            resetPriorityClass: true);

        orphan.Refresh();
        Assert.IsGreaterThanOrEqualTo(1, second.Released);
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityNormal,
            ReadIoPriority(orphan));
        Assert.AreNotEqual(
            Corner,
            (ulong)orphan.ProcessorAffinity.ToInt64(),
            "Sierota z pelnym odciskiem miala odzyskac rdzenie.");
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task ChildOfAParentThatWasLowGetsLowBackNotNormal()
    {
        RequireCorner();

        // Rodzic czyta z priorytetem Low jeszcze zanim Dismode go dotknie.
        // Obnizenie do VeryLow jest dozwolone, dziecko dziedziczy VeryLow.
        // Po sesji rodzic wraca do Low z dziennika — i dziecko ma dostac
        // Low, czyli to, co odziedziczyloby bez nas, a nie stale Normal.
        string triggerFile = Path.Combine(_directory, "spawn.go");
        string childPidFile = Path.Combine(_directory, "child.pid");
        Process parent = StartParentThatSpawnsOnTrigger(triggerFile, childPidFile);
        SetIoPriority(parent, IoPriorityNativeMethods.IoPriorityLow);
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityLow,
            ReadIoPriority(parent),
            "Warunek testu: rodzic startuje z priorytetem I/O Low.");
        nint parentOriginalAffinity = parent.ProcessorAffinity;
        ProcessRuntimeKey parentKey = new(
            parent.Id,
            parent.StartTime.ToUniversalTime());
        using AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundAffinityMask: Corner,
            lowerBackgroundIoPriority: true);

        Assert.IsTrue(
            await actuator.RestrainAsync(parentKey, CancellationToken.None),
            "Nie udalo sie ograniczyc rodzica.");
        parent.Refresh();
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityVeryLow,
            ReadIoPriority(parent),
            "Warunek testu: Low -> VeryLow to obnizenie i ma przejsc.");

        await File.WriteAllTextAsync(triggerFile, "go");
        Process child = await WaitForChildAsync(childPidFile);
        Assert.AreEqual(
            Corner,
            (ulong)child.ProcessorAffinity.ToInt64(),
            "Warunek testu: dziecko mialo odziedziczyc maske.");
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityVeryLow,
            ReadIoPriority(child),
            "Warunek testu: dziecko mialo odziedziczyc VeryLow.");

        Assert.IsTrue(
            await actuator.ReleaseAsync(parentKey, CancellationToken.None),
            "Nie udalo sie zwolnic rodzica.");

        parent.Refresh();
        child.Refresh();
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityLow,
            ReadIoPriority(parent),
            "Rodzic mial wrocic do Low z dziennika.");
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityLow,
            ReadIoPriority(child),
            "Dziecko dostalo stale Normal zamiast wartosci rodzica. "
                + "Odziedziczyloby Low, gdyby nas nie bylo.");
        Assert.AreEqual(
            parentOriginalAffinity,
            child.ProcessorAffinity,
            "Dziecko ma dostac maske, ktora rodzic ma po przywroceniu.");
    }

    private static void RequireCorner()
    {
        if (Environment.ProcessorCount < 8)
        {
            Assert.Inconclusive(
                "Cwiartka 0xF wymaga co najmniej osmiu procesorow logicznych.");
        }
    }

    private Process StartSleeper()
    {
        Process process = StartPowerShell("Start-Sleep -Seconds 120");
        Thread.Sleep(800);
        process.Refresh();
        return process;
    }

    /// <summary>
    /// A process whose parent has already exited: an intermediate PowerShell
    /// starts it, writes its id and leaves.
    /// </summary>
    private async Task<Process> StartOrphanAsync()
    {
        string pidFile = Path.Combine(_directory, "orphan.pid");
        using Process intermediate = StartPowerShell(
            "$p = Start-Process powershell.exe -ArgumentList "
                + "'-NoProfile','-NonInteractive','-Command',"
                + "'Start-Sleep -Seconds 120' -PassThru -WindowStyle Hidden; "
                + $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id");
        _processes.Remove(intermediate);
        Process orphan = await WaitForChildAsync(pidFile);
        intermediate.WaitForExit(15_000);
        Assert.IsTrue(
            intermediate.HasExited,
            "Warunek testu: posrednik mial sie zakonczyc, zeby dziecko bylo "
                + "sierota.");
        return orphan;
    }

    private Process StartParentThatSpawnsOnTrigger(
        string triggerFile,
        string childPidFile)
    {
        Process parent = StartPowerShell(
            $"while (-not (Test-Path -LiteralPath '{triggerFile}')) "
                + "{ Start-Sleep -Milliseconds 200 }; "
                + "$p = Start-Process powershell.exe -ArgumentList "
                + "'-NoProfile','-NonInteractive','-Command',"
                + "'Start-Sleep -Seconds 120' -PassThru -WindowStyle Hidden; "
                + $"Set-Content -LiteralPath '{childPidFile}' -Value $p.Id; "
                + "Start-Sleep -Seconds 120");
        Thread.Sleep(800);
        parent.Refresh();
        return parent;
    }

    private Process StartPowerShell(string command)
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
        startInfo.ArgumentList.Add(command);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        _processes.Add(process);
        return process;
    }

    private async Task<Process> WaitForChildAsync(string pidFile)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (File.Exists(pidFile))
            {
                string text = (await File.ReadAllTextAsync(pidFile, timeout.Token))
                    .Trim();
                if (int.TryParse(text, out int processId))
                {
                    Process child = Process.GetProcessById(processId);
                    _processes.Add(child);
                    // Chwila, zeby Start-Sleep faktycznie ruszyl.
                    await Task.Delay(500, timeout.Token);
                    child.Refresh();
                    return child;
                }
            }

            await Task.Delay(200, timeout.Token);
        }
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

    private static void SetIoPriority(Process process, uint priority)
    {
        int status = IoPriorityNativeMethods.NtSetInformationProcess(
            process.Handle,
            IoPriorityNativeMethods.ProcessIoPriority,
            in priority,
            sizeof(uint));
        Assert.AreEqual(
            IoPriorityNativeMethods.StatusSuccess,
            status,
            $"Ustawienie priorytetu I/O zwrocilo NTSTATUS 0x{status:X8}.");
    }
}

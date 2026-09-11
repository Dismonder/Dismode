using System.Diagnostics;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Data.Journal;
using GameShift.Windows.NativeInterop;
using GameShift.Windows.Processes;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Ograniczenia, ktore proces tla przekazuje swoim dzieciom.
/// <para>
/// Zmierzone osobno na tej maszynie: proces uruchomiony przez rodzica
/// z maska powinowactwa i obnizonym priorytetem wejscia-wyjscia dostaje OBA
/// te ustawienia. Rodzic po sesji wraca do normy, a dziecko zostaje z nimi
/// na zawsze. Przegladarka, Steam i launchery rodza dzieci w trakcie gry,
/// wiec bez tego przegladu zostawialibysmy po sobie spowolnione procesy.
/// </para>
/// <para>
/// Priorytetu wejscia-wyjscia nie pokazuje zadne standardowe narzedzie
/// Windows, wiec uzytkownik nie mialby jak tego u siebie znalezc ani cofnac.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveInheritedRestraintTests
{
    private const ulong Corner = 0xF;
    private Process? _parent;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "gameshift-dziedziczenie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_parent is not null)
        {
            try
            {
                if (!_parent.HasExited)
                {
                    _parent.Kill(entireProcessTree: true);
                    _parent.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                _parent.Dispose();
                _parent = null;
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
    [Timeout(180_000)]
    public async Task ChildBornUnderRestraintIsReleasedWithItsParent()
    {
        if (Environment.ProcessorCount < 8)
        {
            Assert.Inconclusive(
                "Cwiartka 0xF wymaga co najmniej osmiu procesorow logicznych.");
            return;
        }

        // Rodzic czeka, potem rodzi dziecko. Dziecko musi powstac PO
        // nalozeniu ograniczen, bo tylko wtedy je dziedziczy.
        _parent = StartParentThatSpawnsAChild();
        ProcessRuntimeKey parentKey = new(
            _parent.Id,
            _parent.StartTime.ToUniversalTime());

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundAffinityMask: Corner,
            lowerBackgroundIoPriority: true);

        Assert.IsTrue(
            await actuator.RestrainAsync(parentKey, CancellationToken.None),
            "Nie udalo sie ograniczyc procesu rodzica.");

        Process child = await WaitForChildAsync(_parent.Id);
        Assert.AreEqual(
            Corner,
            (ulong)child.ProcessorAffinity.ToInt64(),
            "Dziecko nie odziedziczylo maski, wiec test nie sprawdza tego, "
                + "co mial sprawdzac.");
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityVeryLow,
            ReadIoPriority(child),
            "Dziecko nie odziedziczylo priorytetu wejscia-wyjscia, wiec test "
                + "nie sprawdza tego, co mial sprawdzac.");

        Assert.IsTrue(
            await actuator.ReleaseAsync(parentKey, CancellationToken.None),
            "Nie udalo sie zwolnic procesu rodzica.");

        child.Refresh();
        Assert.AreNotEqual(
            Corner,
            (ulong)child.ProcessorAffinity.ToInt64(),
            "Dziecko zostalo w cwiartce po zwolnieniu rodzica.");
        Assert.AreEqual(
            IoPriorityNativeMethods.IoPriorityNormal,
            ReadIoPriority(child),
            "Dziecko zostalo z obnizonym priorytetem wejscia-wyjscia. Tego "
                + "uzytkownik nie zobaczy w zadnym standardowym narzedziu.");
        child.Dispose();
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

    /// <summary>
    /// The child by its parent, not by its mask. Any other PowerShell on the
    /// machine carrying the corner mask — another test's, a user's — would
    /// otherwise be mistaken for it, and the assertions would be made
    /// against a stranger.
    /// </summary>
    private static async Task<Process> WaitForChildAsync(int parentId)
    {
        ProcessParentMapProvider parents = new();
        for (int attempt = 0; attempt < 60; attempt++)
        {
            foreach ((int childId, int childParentId) in parents.Capture())
            {
                if (childParentId != parentId || childId == parentId)
                {
                    continue;
                }

                Process? candidate = null;
                try
                {
                    candidate = Process.GetProcessById(childId);
                    if (string.Equals(
                            candidate.ProcessName,
                            "powershell",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }

                    candidate.Dispose();
                }
                catch (ArgumentException)
                {
                    candidate?.Dispose();
                }
            }

            await Task.Delay(500);
        }

        Assert.Fail("Dziecko nie pojawilo sie w zalozonym czasie.");
        throw new InvalidOperationException("nieosiagalne");
    }

    private static Process StartParentThatSpawnsAChild()
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
        startInfo.ArgumentList.Add(
            "Start-Sleep -Seconds 6; "
                + "Start-Process powershell.exe -ArgumentList "
                + "'-NoProfile','-NonInteractive','-Command',"
                + "'Start-Sleep -Seconds 90' -WindowStyle Hidden; "
                + "Start-Sleep -Seconds 90");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        Thread.Sleep(1200);
        process.Refresh();
        return process;
    }
}

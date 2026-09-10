using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Data.Journal;
using GameShift.Windows.Cpu;
using GameShift.Windows.Sessions;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Restraint steering a real process onto other processors.
/// <para>
/// The policy declines on a machine whose cores are all equal and share one
/// cache, which is the machine these tests usually run on, so the target
/// processors are supplied directly. Otherwise this path would only ever be
/// exercised on hardware nobody here has.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveBackgroundSteeringTests
{
    private Process? _target;
    private string _directory = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "gameshift-steer-" + Guid.NewGuid().ToString("N"));
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
    public async Task RestrainedProcessIsSteeredAwayAndGivenBackTheMachine()
    {
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        if (topology.Processors.Count < 4)
        {
            Assert.Inconclusive("Potrzebne co najmniej cztery procesory logiczne.");
            return;
        }

        // Druga polowa maszyny gra role rdzeni "poza gra".
        uint[] background = [.. topology.Processors
            .Skip(topology.Processors.Count / 2)
            .Select(processor => processor.Id)];

        _target = StartSpinner();
        ProcessRuntimeKey key = new(
            _target.Id,
            _target.StartTime.ToUniversalTime());

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundCpuSetIds: background);

        bool restrained = await actuator.RestrainAsync(
            key,
            CancellationToken.None);

        Assert.IsTrue(restrained, "Nie udalo sie ograniczyc procesu.");
        _target.Refresh();
        Assert.AreEqual(
            ProcessPriorityClass.BelowNormal,
            _target.PriorityClass,
            "Priorytet nie zostal obnizony.");
        IReadOnlyList<uint>? steered = ProcessCpuSets.TryRead(_target.Id);
        Assert.IsNotNull(steered);
        CollectionAssert.AreEquivalent(
            background,
            steered.ToArray(),
            "Proces nie zostal odsuniety na wskazane procesory.");

        bool released = await actuator.ReleaseAsync(key, CancellationToken.None);

        Assert.IsTrue(released, "Nie udalo sie zwolnic procesu.");
        _target.Refresh();
        Assert.AreEqual(
            ProcessPriorityClass.Normal,
            _target.PriorityClass,
            "Priorytet nie wrocil.");
        IReadOnlyList<uint>? cleared = ProcessCpuSets.TryRead(_target.Id);
        Assert.IsNotNull(cleared);
        Assert.IsEmpty(
            cleared,
            "Procesowi nie oddano calej maszyny.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task SteeringNeverNarrowsTheProcessAffinityMask()
    {
        // Sterowanie zbiorami ma pozostac podpowiedzia — zwezanie maski jest
        // osobna, jawnie podana decyzja, ktora sprawdza test nizej. Tutaj
        // aktuator nie dostaje maski, wiec nie ma prawa niczego zwezic.
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        if (topology.Processors.Count < 4)
        {
            Assert.Inconclusive("Potrzebne co najmniej cztery procesory logiczne.");
            return;
        }

        _target = StartSpinner();
        _target.Refresh();
        ulong maskBefore = (ulong)_target.ProcessorAffinity.ToInt64();

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery.jsonl"));
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundCpuSetIds: [topology.Processors[^1].Id]);

        await actuator.RestrainAsync(
            new(_target.Id, _target.StartTime.ToUniversalTime()),
            CancellationToken.None);

        _target.Refresh();
        Assert.AreEqual(
            maskBefore,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Sterowanie zwezilo maske affinity procesu.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task HardMaskConfinesTheProcessAndIsPutBackOnRelease()
    {
        // To jest mechanizm, ktory realnie rusza czasy klatek. Zmierzone na
        // 7 Days To Die pod pelnym obciazeniem: samo obnizenie priorytetu
        // przesunelo p99 z 16,80 ms na 16,36 ms, czyli w granicach szumu,
        // a zamkniecie tla w cwiartce maszyny na 10,65 ms. Roznica bierze sie
        // stad, ze priorytet planista wazy, a maski zlamac nie moze — wiec
        // maska musi realnie zwezic proces, i musi wrocic.
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        if (topology.Processors.Count < 4)
        {
            Assert.Inconclusive(
                "Potrzebne co najmniej cztery procesory logiczne.");
            return;
        }

        _target = StartSpinner();
        _target.Refresh();
        ulong maskBefore = (ulong)_target.ProcessorAffinity.ToInt64();
        ulong corner = 0b11;
        Assert.AreNotEqual(
            maskBefore,
            corner,
            "Proces juz siedzi w rogu maszyny; test nic by nie pokazal.");

        AppendOnlyRecoveryJournal journal =
            new(Path.Combine(_directory, "recovery-mask.jsonl"));
        ProcessRuntimeKey key =
            new(_target.Id, _target.StartTime.ToUniversalTime());
        JournaledProBalanceActuator actuator = new(
            journal,
            new SessionId(Guid.NewGuid()),
            backgroundAffinityMask: corner);

        Assert.IsTrue(
            await actuator.RestrainAsync(key, CancellationToken.None),
            "Ograniczenie nie doszlo do skutku.");

        _target.Refresh();
        Assert.AreEqual(
            corner,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Maska nie zostala nalozona, wiec planista dalej moze dac temu "
                + "procesowi kazdy rdzen.");

        Assert.IsTrue(
            await actuator.ReleaseAsync(key, CancellationToken.None),
            "Zwolnienie nie doszlo do skutku.");

        _target.Refresh();
        Assert.AreEqual(
            maskBefore,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Maska nie wrocila. Proces zostalby zamkniety w rogu maszyny "
                + "po zakonczeniu sesji.");
    }

    private static Process StartSpinner()
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
            "$end = (Get-Date).AddMinutes(2); "
            + "while ((Get-Date) -lt $end) { $null = 1 }");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        Thread.Sleep(1200);
        process.Refresh();
        return process;
    }
}

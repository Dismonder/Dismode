using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Default CPU sets against a real process. Unlike an affinity mask these are
/// a preference rather than a rule, so nothing here should ever be able to
/// starve the target — which is exactly why they are worth preferring.
/// </summary>
[TestClass]
public sealed class LiveCpuSetTests
{
    private Process? _target;

    [TestCleanup]
    public void Cleanup()
    {
        if (_target is null)
        {
            return;
        }

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

    [TestMethod]
    [Timeout(120_000)]
    public void SetsAreAppliedReadBackAndCleared()
    {
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        Assert.IsGreaterThan(1, topology.Processors.Count);

        _target = StartIdleProcess();

        // Stan wyjsciowy: brak przypisania oznacza cala maszyne.
        IReadOnlyList<uint>? before = ProcessCpuSets.TryRead(_target.Id);
        Assert.IsNotNull(before, "Nie udalo sie odczytac domyslnych zbiorow.");
        Assert.IsEmpty(before);

        uint[] half = [.. topology.Processors
            .Take(Math.Max(1, topology.Processors.Count / 2))
            .Select(processor => processor.Id)];

        Assert.IsTrue(
            ProcessCpuSets.TryApply(_target.Id, half),
            "Nie udalo sie ustawic domyslnych zbiorow procesorow.");

        IReadOnlyList<uint>? applied = ProcessCpuSets.TryRead(_target.Id);
        Assert.IsNotNull(applied);
        CollectionAssert.AreEquivalent(half, applied.ToArray());

        // Wyczyszczenie oddaje procesowi cala maszyne.
        Assert.IsTrue(ProcessCpuSets.TryApply(_target.Id, []));
        IReadOnlyList<uint>? cleared = ProcessCpuSets.TryRead(_target.Id);
        Assert.IsNotNull(cleared);
        Assert.IsEmpty(cleared);
    }

    [TestMethod]
    public void ExistingHardAffinityIsRecognisedAsDefeatingSets()
    {
        // To jest pulapka, przez ktora sam odczyt zwrotny nie wystarcza:
        // proces z zawezonym affinity ignoruje domyslne zbiory, a mimo to
        // odczyt zwraca to, co zapisano.
        Assert.IsTrue(
            ProcessCpuSets.WouldBeDefeatedByAffinity(
                currentAffinityMask: 0b0011,
                desiredMask: 0b1100));
        Assert.IsTrue(
            ProcessCpuSets.WouldBeDefeatedByAffinity(
                currentAffinityMask: 0b0011,
                desiredMask: 0b0111));

        // Zadanie mieszczace sie w obecnej masce jest bezpieczne.
        Assert.IsFalse(
            ProcessCpuSets.WouldBeDefeatedByAffinity(
                currentAffinityMask: 0b1111,
                desiredMask: 0b0011));

        // Brak ograniczenia w ogole.
        Assert.IsFalse(
            ProcessCpuSets.WouldBeDefeatedByAffinity(
                currentAffinityMask: 0,
                desiredMask: 0b0011));
    }

    [TestMethod]
    [Timeout(120_000)]
    public void SetsDoNotNarrowTheAffinityMask()
    {
        // Roznica wobec twardego affinity: zbiory sa podpowiedzia, wiec maska
        // procesu ma pozostac nietknieta i harmonogram wciaz moze wyjsc poza
        // wskazane procesory, gdy musi.
        CpuTopology topology = SystemCpuTopologyProvider.Read()!;
        _target = StartIdleProcess();
        _target.Refresh();
        ulong maskBefore = (ulong)_target.ProcessorAffinity.ToInt64();

        uint[] one = [topology.Processors[0].Id];
        Assert.IsTrue(ProcessCpuSets.TryApply(_target.Id, one));

        _target.Refresh();
        Assert.AreEqual(
            maskBefore,
            (ulong)_target.ProcessorAffinity.ToInt64(),
            "Domyslne zbiory zmienily maske affinity, a nie powinny.");
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
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 120");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic procesu testowego.");
        Thread.Sleep(1000);
        process.Refresh();
        return process;
    }
}

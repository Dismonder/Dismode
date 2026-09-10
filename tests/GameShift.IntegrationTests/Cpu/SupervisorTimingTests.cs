using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// How long one pass actually takes on this machine. The loop is meant to run
/// every couple of seconds for the whole session, so a pass that takes longer
/// than its own interval is not a slow test — it is a design fault that would
/// eat a core of the machine it is supposed to be protecting.
/// </summary>
[TestClass]
public sealed class SupervisorTimingTests
{
    [TestMethod]
    [Timeout(120_000)]
    public void GeneralInventoryStaysUsableForDiagnostics()
    {
        // Pelna inwentaryzacja nie jest juz uzywana przez petle — ta ma
        // wlasny lekki probnik — ale nadal zasila diagnostyke, wiec jej koszt
        // wciaz ma znaczenie.
        ProcessInventory inventory = new();
        // Pierwsze przejscie rozgrzewa cache systemu; mierzymy nastepne.
        _ = inventory.Capture();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProcessSnapshot> snapshot = inventory.Capture();
        stopwatch.Stop();

        TestContext.WriteLine(
            $"Inwentaryzacja {snapshot.Count} procesow: "
            + $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.IsGreaterThan(0, snapshot.Count);
        Assert.IsLessThan(
            1500,
            stopwatch.ElapsedMilliseconds,
            $"Inwentaryzacja {snapshot.Count} procesow zajela "
                + $"{stopwatch.ElapsedMilliseconds} ms.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public void LeanSamplerCostsLessThanTheGeneralInventory()
    {
        // Petla potrzebuje czterech pol. Pelna inwentaryzacja czyta dla kazdego
        // procesu dodatkowo sciezke pliku, uchwyt glownego okna i klase
        // priorytetu — kazde z nich kosztuje uchwyt albo wyliczanie okien.
        ProcessInventory full = new();
        CpuProcessSampler lean = new();
        _ = full.Capture();
        _ = lean.Capture();

        Stopwatch fullClock = Stopwatch.StartNew();
        IReadOnlyList<ProcessSnapshot> fullSnapshot = full.Capture();
        fullClock.Stop();

        Stopwatch leanClock = Stopwatch.StartNew();
        IReadOnlyList<CpuProcessSample> leanSnapshot = lean.Capture();
        leanClock.Stop();

        TestContext.WriteLine(
            $"Pelna inwentaryzacja: {fullSnapshot.Count} procesow, "
            + $"{fullClock.Elapsed.TotalMilliseconds:F1} ms");
        TestContext.WriteLine(
            $"Lekki probnik:        {leanSnapshot.Count} procesow, "
            + $"{leanClock.Elapsed.TotalMilliseconds:F1} ms");

        Assert.IsGreaterThan(0, leanSnapshot.Count);
        Assert.IsLessThan(
            fullClock.Elapsed.TotalMilliseconds,
            leanClock.Elapsed.TotalMilliseconds,
            "Lekki probnik nie jest tanszy od pelnej inwentaryzacji.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task OneSupervisorPassFitsInsideItsInterval()
    {
        await using ProBalanceSupervisor supervisor = new(
            new CpuProcessSampler(),
            new CountingActuator(),
            static () => new HashSet<int>(),
            settings: new ProBalanceSettings { BackgroundLoadCores = 1000 });

        // Pierwsze przejscie tylko ustala punkt odniesienia dla obciazenia
        // systemu, a bez odstepu drugi odczyt trafia w ten sam takt zegara,
        // roznice wychodza zerowe i nadzorca wychodzi przed inwentaryzacja.
        // Bez tej przerwy pomiar dotyczylby przejscia, ktore nic nie robi.
        await supervisor.TickAsync(CancellationToken.None);
        await Task.Delay(250);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProBalanceDecision> decisions =
            await supervisor.TickAsync(CancellationToken.None);
        stopwatch.Stop();

        double perPass = stopwatch.Elapsed.TotalMilliseconds;
        Assert.IsEmpty(
            decisions,
            "Przy nieosiagalnym progu obciazenia nie moze zapasc decyzja.");
        Assert.IsGreaterThan(
            0.5,
            perPass,
            "Przejscie zajelo mniej niz pol milisekundy, wiec zapewne nic nie "
                + "zrobilo — pomiar bylby pusty.");
        TestContext.WriteLine(
            $"Jedno przejscie nadzorcy: {perPass:F1} ms");
        TestContext.WriteLine(
            "Udzial w jednym rdzeniu przy interwale 2 s: "
            + $"{100.0 * perPass / 2000.0:F2}%");

        Assert.IsLessThan(
            2000,
            stopwatch.ElapsedMilliseconds,
            $"Jedno przejscie zajelo {stopwatch.ElapsedMilliseconds} ms.");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class CountingActuator : IProBalanceActuator
    {
        public ValueTask<bool> RestrainAsync(
            GameShift.Core.Domain.Processes.ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> ReleaseAsync(
            GameShift.Core.Domain.Processes.ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}

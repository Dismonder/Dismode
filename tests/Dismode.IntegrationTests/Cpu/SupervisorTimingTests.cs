using System.Diagnostics;
using Dismode.Core.Cpu;
using Dismode.Windows.Cpu;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Cpu;

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
    public void SystemInformationPathAgreesWithTheProcessApi()
    {
        // Szybka sciezka ma zastapic te przez uchwyty bez zmiany tego, co
        // widzi petla: te same procesy, te same nazwy i — co najwazniejsze —
        // dokladnie te same czasy startu, bo aktuator dopasowuje probki do
        // tozsamosci procesow przez rownosc tej wartosci.
        CpuProcessSampler sampler = new();
        IReadOnlyList<CpuProcessSample>? fast =
            sampler.TryCaptureViaSystemInformation();
        Stopwatch gap = Stopwatch.StartNew();
        IReadOnlyList<CpuProcessSample> reference =
            CpuProcessSampler.CaptureViaProcessApi();
        gap.Stop();

        Assert.IsNotNull(fast, "NtQuerySystemInformation odmowil.");
        Dictionary<int, CpuProcessSample> byId = fast.ToDictionary(
            sample => sample.ProcessId);

        CpuProcessSample self = byId[Environment.ProcessId];
        using Process current = Process.GetCurrentProcess();
        Assert.AreEqual(current.ProcessName, self.Name);
        Assert.AreEqual(
            new DateTimeOffset(
                current.StartTime.ToUniversalTime(),
                TimeSpan.Zero),
            self.StartedAtUtc,
            "Czas startu wlasnego procesu rozni sie od Process.StartTime.");

        int compared = 0;
        int mismatchedStart = 0;
        List<string> nameMismatches = [];
        // Miedzy dwoma odczytami kazdy proces mogl zuzyc najwyzej tyle czasu
        // procesora, ile uplynelo, razy liczba watkow, ktore mogl zajac.
        TimeSpan tolerance = gap.Elapsed * Environment.ProcessorCount
            + TimeSpan.FromMilliseconds(50);
        foreach (CpuProcessSample expected in reference)
        {
            if (!byId.TryGetValue(expected.ProcessId, out CpuProcessSample? actual)
                || actual.StartedAtUtc != expected.StartedAtUtc)
            {
                // Proces mogl sie zakonczyc miedzy odczytami, a jego numer
                // trafic do nastepcy; taki wpis nie jest porownywalny.
                if (actual is not null)
                {
                    mismatchedStart++;
                }

                continue;
            }

            compared++;
            if (!string.Equals(
                    actual.Name,
                    expected.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                nameMismatches.Add($"{expected.ProcessId}: {actual.Name} / {expected.Name}");
            }

            TimeSpan drift = expected.TotalProcessorTime - actual.TotalProcessorTime;
            Assert.IsTrue(
                drift >= TimeSpan.Zero && drift <= tolerance,
                $"Czas procesora {expected.Name} ({expected.ProcessId}): szybka "
                    + $"sciezka {actual.TotalProcessorTime}, uchwyty "
                    + $"{expected.TotalProcessorTime}, tolerancja {tolerance}.");
        }

        TestContext.WriteLine(
            $"Szybka sciezka: {fast.Count} procesow, uchwyty: "
                + $"{reference.Count}, porownano {compared}, "
                + $"niezgodny start: {mismatchedStart}");
        Assert.IsGreaterThanOrEqualTo(reference.Count * 9 / 10, compared);
        Assert.IsEmpty(
            nameMismatches,
            "Nazwy rozne od Process.ProcessName: "
                + string.Join("; ", nameMismatches));
        Assert.IsLessThanOrEqualTo(
            2,
            mismatchedStart,
            "Zbyt wiele procesow z innym czasem startu niz Process.StartTime.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public void SystemInformationPathIsCheaperThanTheProcessApi()
    {
        // Mediana z wielu przejsc, nie jeden pomiar: pojedyncze przejscie
        // trafia w cudzy przydzial czasu albo w zerowanie stron i klamie
        // w obie strony. Porownanie jest w dodatku nierowne na korzysc
        // uchwytow — nieuprzywilejowany proces testu otwiera tylko czesc
        // procesow, a wywolanie systemowe widzi wszystkie.
        const int rounds = 15;
        CpuProcessSampler sampler = new();
        _ = sampler.TryCaptureViaSystemInformation();
        _ = CpuProcessSampler.CaptureViaProcessApi();

        List<double> fastTimes = [];
        List<double> slowTimes = [];
        int fastCount = 0;
        int slowCount = 0;
        for (int round = 0; round < rounds; round++)
        {
            Stopwatch fastClock = Stopwatch.StartNew();
            IReadOnlyList<CpuProcessSample>? fast =
                sampler.TryCaptureViaSystemInformation();
            fastClock.Stop();
            Assert.IsNotNull(fast);
            fastCount = fast.Count;
            fastTimes.Add(fastClock.Elapsed.TotalMilliseconds);

            Stopwatch slowClock = Stopwatch.StartNew();
            IReadOnlyList<CpuProcessSample> slow =
                CpuProcessSampler.CaptureViaProcessApi();
            slowClock.Stop();
            slowCount = slow.Count;
            slowTimes.Add(slowClock.Elapsed.TotalMilliseconds);
        }

        double fastMedian = Median(fastTimes);
        double slowMedian = Median(slowTimes);
        TestContext.WriteLine(
            $"NtQuerySystemInformation: {fastCount} procesow, mediana "
                + $"{fastMedian:F2} ms ({fastMedian / fastCount * 1000:F1} us "
                + "na proces)");
        TestContext.WriteLine(
            $"Process API:              {slowCount} procesow, mediana "
                + $"{slowMedian:F2} ms ({slowMedian / slowCount * 1000:F1} us "
                + "na proces)");
        Assert.IsLessThan(
            slowMedian,
            fastMedian,
            "Jedno wywolanie systemowe nie jest tansze od uchwytu na proces.");
    }

    private static double Median(List<double> values)
    {
        List<double> sorted = [.. values];
        sorted.Sort();
        int middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
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
        double intervalMilliseconds =
            ProBalanceSupervisor.DefaultInterval.TotalMilliseconds;
        TestContext.WriteLine(
            "Udzial w jednym rdzeniu przy interwale "
            + $"{intervalMilliseconds / 1000:F0} s: "
            + $"{100.0 * perPass / intervalMilliseconds:F2}%");

        // Przejscie ma sie miescic w ulamku interwalu, nie w calym: petla,
        // ktora zjada dziesiata czesc rdzenia, sama psuje to, czego pilnuje.
        Assert.IsLessThan(
            intervalMilliseconds / 10,
            perPass,
            $"Jedno przejscie zajelo {perPass:F1} ms przy interwale "
                + $"{intervalMilliseconds:F0} ms.");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class CountingActuator : IProBalanceActuator
    {
        public ValueTask<bool> RestrainAsync(
            Dismode.Core.Domain.Processes.ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> ReleaseAsync(
            Dismode.Core.Domain.Processes.ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}

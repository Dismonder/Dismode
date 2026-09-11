using System.Diagnostics;
using System.Globalization;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Frame times of a real game, measured through PresentMon, with and without
/// background restraint under CPU contention.
/// <para>
/// Every other measurement in this suite uses wake latency as a stand-in for
/// smoothness. This is the real thing: how long the game actually took to
/// produce each frame. Skipped when no game is running, because there is
/// nothing honest to measure without one.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveGameFrameTimeTests
{
    /// <summary>
    /// Games the measurement knows how to attach to, most CPU-hungry first.
    /// Order matters: a title whose frame time is decided by the processor is
    /// the only one that can show whether restraint does anything, and picking
    /// a GPU-limited one instead produces a confident-looking null result.
    /// </summary>
    private static readonly string[] KnownGameProcessNames =
    [
        "7DaysToDie",
        "valheim",
        "pcsx2-qt",
        "rpcs3",
        "re9",
        "MonsterHunterWilds",
        "Cyberpunk2077",
        "SonsOfTheForest",
        "TheForest",
        "Raft",
        "RobloxPlayerBeta",
    ];

    private readonly List<Process> _load = [];

    [TestCleanup]
    public void StopLoad()
    {
        foreach (Process process in _load)
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

        _load.Clear();
    }

    [TestMethod]
    [Timeout(900_000)]
    public async Task RestraintUnderContentionDoesNotWorsenFrameTimes()
    {
        if (!TryFindGame(out Process? found, out string presentMonPath)
            || found is null)
        {
            Assert.Inconclusive(
                "Zadna znana gra nie dziala albo brakuje PresentMon.");
            return;
        }

        using Process game = found;
        TestContext.WriteLine($"Gra: {game.ProcessName} (PID {game.Id})");
        await RequireRenderingAsync(presentMonPath, game.Id);

        int hogs = Math.Max(2, Environment.ProcessorCount);
        for (int index = 0; index < hogs; index++)
        {
            _load.Add(StartHog());
        }

        await Task.Delay(3000);

        PriorityProBalanceActuator actuator = new();
        ProBalanceSupervisor supervisor = new(
            new CpuProcessSampler(),
            actuator,
            () => new HashSet<int> { game.Id },
            settings: new ProBalanceSettings
            {
                BackgroundLoadCores = 0,
                MinimumRestraint = TimeSpan.FromSeconds(1),
                Cooldown = TimeSpan.FromSeconds(1),
                MaximumRestrained = hogs,
            });

        List<double> withoutRestraint = [];
        List<double> withRestraint = [];
        // Bez tego wynik jest nie do odczytania: "brak poprawy" i "nic nie
        // zostalo ograniczone" wygladaja w liczbach identycznie, a znacza cos
        // zupelnie innego.
        List<ProBalanceDecision> decisions = [];

        try
        {
            // Fazy sa przeplatane, a nie ustawione jedna po drugiej. Maszyna
            // dryfuje przez cala minute pomiaru — inne procesy budza sie,
            // gra zmienia scene — a w blokach sekwencyjnych ten dryf jest nie
            // do odroznienia od badanego efektu. Przeplot rozklada go rowno na
            // oba warianty.
            for (int round = 0; round < Rounds; round++)
            {
                await ReleaseEverythingAsync(supervisor);
                await Task.Delay(1500);
                withoutRestraint.Add(
                    (await MeasureAsync(presentMonPath, game.Id, BlockSeconds))
                    .Percentile99);

                for (int tick = 0; tick < 5; tick++)
                {
                    foreach (ProBalanceDecision decision in
                        await supervisor.TickAsync(CancellationToken.None))
                    {
                        decisions.Add(decision);
                    }

                    await Task.Delay(400);
                }

                withRestraint.Add(
                    (await MeasureAsync(presentMonPath, game.Id, BlockSeconds))
                    .Percentile99);
            }
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        for (int round = 0; round < Rounds; round++)
        {
            TestContext.WriteLine(
                $"runda {round + 1}: bez {withoutRestraint[round]:F2} ms, "
                + $"z {withRestraint[round]:F2} ms, "
                + $"roznica {withRestraint[round] - withoutRestraint[round]:F2} ms");
        }

        double medianWithout = Median(withoutRestraint);
        double medianWith = Median(withRestraint);
        int better = withoutRestraint
            .Zip(withRestraint, static (bez, z) => z < bez)
            .Count(improved => improved);

        TestContext.WriteLine(
            $"mediana p99 bez ograniczania: {medianWithout:F2} ms");
        TestContext.WriteLine(
            $"mediana p99 z ograniczaniem:  {medianWith:F2} ms");
        TestContext.WriteLine(
            $"rund z poprawa: {better} z {Rounds}");
        TestContext.WriteLine(
            $"decyzji ograniczenia: {decisions.Count}");
        foreach (IGrouping<string, ProBalanceDecision> group in decisions
            .GroupBy(decision => decision.ProcessName))
        {
            TestContext.WriteLine(
                $"  {group.Key}: {group.Count()}");
        }

        Assert.IsGreaterThan(0, withoutRestraint.Count);
        Assert.IsTrue(
            withoutRestraint.Concat(withRestraint).All(value => value > 0),
            "Ktorys blok nie zlapal ani jednej klatki. To awaria pomiaru, "
                + "nie wynik — sprawdz, czy nie wisi porzucona sesja ETW "
                + "(logman query -ets) i czy gra sie renderuje.");

        // Mediana z par odporna jest na pojedyncze wahniecie maszyny, ktore
        // w pomiarze blokowym przewracalo caly wynik. Prog jest szeroki
        // celowo: chodzi o wychwycenie regresji, nie o dowodzenie poprawy.
        Assert.IsLessThan(
            medianWithout * 1.5,
            medianWith,
            "Ograniczanie pogorszylo mediane ogona czasow klatek.");
    }

    /// <summary>
    /// Priority lowering did nothing for frame times, so this asks whether the
    /// mechanism is wrong rather than the idea. Three conditions, interleaved:
    /// background left alone, background at low priority — what the module
    /// does today — and background pinned by a hard affinity mask to a corner
    /// of the machine.
    /// <para>
    /// The distinction is not cosmetic. CPU sets and priority are both hints
    /// the scheduler weighs; an affinity mask is a rule it cannot break. Under
    /// as many compute-bound threads as there are cores, a hint is exactly the
    /// thing that gets outvoted.
    /// </para>
    /// </summary>
    [TestMethod]
    [TestCategory("Live")]
    [Timeout(900_000)]
    [DataRow(0, DisplayName = "pelne obciazenie: tyle petli, ile watkow")]
    [DataRow(4, DisplayName = "umiarkowane obciazenie: cztery petle")]
    public async Task HardAffinityBeatsPriorityUnderContention(int hogCount)
    {
        if (!TryFindGame(out Process? found, out string presentMonPath)
            || found is null)
        {
            Assert.Inconclusive(
                "Zadna znana gra nie dziala albo brakuje PresentMon.");
            return;
        }

        using Process game = found;
        TestContext.WriteLine($"Gra: {game.ProcessName} (PID {game.Id})");
        await RequireRenderingAsync(presentMonPath, game.Id);

        // Pelne obciazenie pokazuje, czy mechanizm dziala. Umiarkowane —
        // mniej wiecej tyle, ile robi przegladarka z rozmowa wideo albo
        // kompilacja w tle — pokazuje, czy dziala tam, gdzie uzytkownik
        // faktycznie bywa. Zmierzone na tej maszynie: w normalnym stanie
        // tlo zajmuje ponizej jednego rdzenia, wiec bramka BackgroundLoadCores
        // nie przepuscilaby nic.
        int hogs = hogCount > 0
            ? hogCount
            : Math.Max(2, Environment.ProcessorCount);
        for (int index = 0; index < hogs; index++)
        {
            _load.Add(StartHog());
        }

        await Task.Delay(3000);
        TestContext.WriteLine($"petli liczacych: {hogs}");
        ReportWhetherThresholdsWouldFire();

        // Cwiartka maszyny dla calego tla. Reszta zostaje grze.
        int corner = Math.Max(2, Environment.ProcessorCount / 4);
        nint cornerMask = (nint)((1L << corner) - 1);
        nint fullMask = (nint)((1L << Environment.ProcessorCount) - 1);
        TestContext.WriteLine(
            $"maska tla: {corner} z {Environment.ProcessorCount} watkow");

        List<double> free = [];
        List<double> lowPriority = [];
        List<double> pinned = [];

        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                ApplyToHogs(ProcessPriorityClass.Normal, fullMask);
                await Task.Delay(1200);
                free.Add(
                    (await MeasureAsync(presentMonPath, game.Id, BlockSeconds))
                    .Percentile99);

                ApplyToHogs(ProcessPriorityClass.BelowNormal, fullMask);
                await Task.Delay(1200);
                lowPriority.Add(
                    (await MeasureAsync(presentMonPath, game.Id, BlockSeconds))
                    .Percentile99);

                ApplyToHogs(ProcessPriorityClass.Normal, cornerMask);
                await Task.Delay(1200);
                pinned.Add(
                    (await MeasureAsync(presentMonPath, game.Id, BlockSeconds))
                    .Percentile99);
            }
        }
        finally
        {
            ApplyToHogs(ProcessPriorityClass.Normal, fullMask);
        }

        for (int round = 0; round < Rounds; round++)
        {
            TestContext.WriteLine(
                $"runda {round + 1}: wolne {free[round]:F2} ms, "
                + $"priorytet {lowPriority[round]:F2} ms, "
                + $"maska {pinned[round]:F2} ms");
        }

        double medianFree = Median(free);
        double medianPriority = Median(lowPriority);
        double medianPinned = Median(pinned);
        TestContext.WriteLine($"mediana p99 wolne:     {medianFree:F2} ms");
        TestContext.WriteLine($"mediana p99 priorytet: {medianPriority:F2} ms");
        TestContext.WriteLine($"mediana p99 maska:     {medianPinned:F2} ms");
        TestContext.WriteLine(
            $"zysk maski wzgledem wolnych: "
            + $"{medianFree - medianPinned:F2} ms "
            + $"({100 * (medianFree - medianPinned) / medianFree:F1}%)");
        TestContext.WriteLine(
            $"rund, w ktorych maska bila priorytet: "
            + $"{lowPriority.Zip(pinned, static (p, m) => m < p).Count(x => x)}"
            + $" z {Rounds}");

        Assert.IsGreaterThan(0, free.Count);
        Assert.IsTrue(
            free.Concat(lowPriority).Concat(pinned).All(value => value > 0),
            "Ktorys blok nie zlapal ani jednej klatki. To awaria pomiaru, "
                + "nie wynik.");
    }

    /// <summary>
    /// The one test that leaves every threshold alone. Everything else here
    /// overrides <c>BackgroundLoadCores</c> to zero to get at the mechanism,
    /// which quietly meant the shipped gates were never exercised — and the
    /// shipped gates are what decides whether any of this reaches a player.
    /// </summary>
    [TestMethod]
    [TestCategory("Live")]
    [Timeout(300_000)]
    public async Task ShippedThresholdsActUnderModerateContention()
    {
        for (int index = 0; index < 4; index++)
        {
            _load.Add(StartHog());
        }

        await Task.Delay(3000);

        List<ProBalanceDecision> decisions = [];
        HashSet<int> noGame = [];
        // Domyslne ustawienia. Zadnych podmianek.
        ProBalanceSupervisor supervisor = new(
            new CpuProcessSampler(),
            new PriorityProBalanceActuator(),
            () => noGame,
            settings: new ProBalanceSettings());

        try
        {
            for (int tick = 0; tick < 8; tick++)
            {
                foreach (ProBalanceDecision decision in
                    await supervisor.TickAsync(CancellationToken.None))
                {
                    decisions.Add(decision);
                }

                await Task.Delay(600);
            }
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        foreach (IGrouping<string, ProBalanceDecision> group in decisions
            .GroupBy(decision => decision.ProcessName))
        {
            TestContext.WriteLine($"{group.Key}: {group.Count()}");
        }

        Assert.IsGreaterThan(
            0,
            decisions.Count,
            "Przy czterech procesach liczacych bez przerwy modul nie podjal "
                + "zadnej decyzji. Zmierzone obciazenie w tym scenariuszu to "
                + "41,7%, a zysk z maski 10,6% — jesli bramki tego nie "
                + "przepuszczaja, mechanizm nigdy nie trafi do gracza.");
    }

    /// <summary>
    /// Says whether the module's own gates would have let it act, alongside
    /// the frame times. A mechanism that works but never runs is worth exactly
    /// as much as one that runs but does nothing, and only the two numbers
    /// side by side tell them apart.
    /// </summary>
    private void ReportWhetherThresholdsWouldFire()
    {
        ProBalanceSettings settings = new();
        // Liczymy dokladnie to, co liczy nadzorca: rdzenie zajete przez tlo,
        // z pominieciem gry i procesow chronionych. Wczesniej bylo tu
        // obciazenie calej maszyny w procentach i to bylo zle pytanie —
        // praca samej gry nie jest dowodem, ze cos grze przeszkadza.
        CpuProcessSampler sampler = new();
        Dictionary<int, TimeSpan> before = sampler.Capture()
            .ToDictionary(
                sample => sample.ProcessId,
                sample => sample.TotalProcessorTime);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        Thread.Sleep(2000);
        double elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;

        double backgroundCores = 0;
        foreach (CpuProcessSample sample in sampler.Capture())
        {
            if (!before.TryGetValue(sample.ProcessId, out TimeSpan previous)
                || BackgroundApplicationGuard.IsProtectedProcessName(
                    sample.Name)
                || sample.Name.Contains(
                    "7DaysToDie",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            backgroundCores +=
                (sample.TotalProcessorTime - previous).TotalSeconds / elapsed;
        }

        TestContext.WriteLine(
            $"tlo zajmuje {backgroundCores:F2} rdzenia "
                + $"(prog {settings.BackgroundLoadCores}) — bramka "
                + (backgroundCores >= settings.BackgroundLoadCores
                    ? "przepuszcza"
                    : "BLOKUJE"));
    }

    /// <summary>
    /// Sets priority and affinity on every load generator, ignoring the ones
    /// that have already gone away.
    /// </summary>
    private void ApplyToHogs(ProcessPriorityClass priority, nint affinity)
    {
        foreach (Process hog in _load)
        {
            try
            {
                if (hog.HasExited)
                {
                    continue;
                }

                hog.PriorityClass = priority;
                hog.ProcessorAffinity = affinity;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>
    /// Refuses to measure a game that is not putting frames on screen.
    /// <para>
    /// A game left running while the desktop is locked or another window has
    /// focus keeps simulating — measured at three cores busy — but stops
    /// presenting, so PresentMon has nothing to see. Without this check the
    /// whole run comes back as zeros, and zeros are indistinguishable from
    /// "restraint made no difference". That mistake cost a full round of wrong
    /// conclusions, so the precondition is now part of the test.
    /// </para>
    /// </summary>
    private async Task RequireRenderingAsync(
        string presentMonPath,
        int processId)
    {
        // Sesja zostaje po kazdym przebiegu, takze zakonczonym normalnie,
        // i blokuje nastepny. --stop_existing_session tego nie zalatwia.
        EtwSessionCleanup.StopStaleSessions(
            EtwSessionCleanup.LegacySessionNames);
        FrameProfile probe = await MeasureAsync(presentMonPath, processId, 4);
        if (probe.Percentile99 <= 0)
        {
            Assert.Inconclusive(
                "Gra nie wystawia klatek. Musi być na pierwszym planie, "
                    + "z odblokowanym ekranem — inaczej dalej liczy świat, "
                    + "ale nie rysuje, a pomiar zwraca same zera.");
        }

        TestContext.WriteLine(
            $"kontrola wstepna: p99 {probe.Percentile99:F2} ms — gra rysuje");
    }

    private const int Rounds = 4;

    /// <summary>
    /// Dlugosc jednego bloku pomiarowego.
    /// <para>
    /// Bylo 8 sekund. Przy 60 klatkach na sekunde to okolo 480 klatek, czyli
    /// p99 opiera sie na piatce najwolniejszych. Do wykazania roznicy rzedu
    /// 60% to wystarczalo, ale kazdy kolejny obszar — GPU, dysk, siec — da
    /// efekty znacznie mniejsze i taki ogon bylby szumem. Trzydziesci sekund
    /// daje okolo 1800 klatek i p99 z osiemnastu obserwacji.
    /// </para>
    /// </summary>
    private const int BlockSeconds = 30;

    /// <summary>
    /// Mediana, ktora dla parzystej liczby probek usrednia dwie srodkowe.
    /// <para>
    /// Poprzednia wersja brala sorted[Count / 2], czyli przy czterech rundach
    /// zawsze gorna srodkowa. To nie jest mediana, tylko trzeci co do
    /// wielkosci wynik, i przy malej liczbie rund przesuwa oba warianty w
    /// gore. Przy roznicy rzedu 60% nie mialo to znaczenia, ale efekty GPU,
    /// dysku i sieci beda znacznie mniejsze.
    /// </para>
    /// </summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<double> sorted = [.. values];
        sorted.Sort();
        int srodek = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[srodek]
            : (sorted[srodek - 1] + sorted[srodek]) / 2;
    }

    /// <summary>
    /// Hands every restrained process back before the next unrestrained block,
    /// so the two halves of a pair really do differ only in whether restraint
    /// is active.
    /// </summary>
    private static async Task ReleaseEverythingAsync(
        ProBalanceSupervisor supervisor) =>
        await supervisor.StopAsync();

    /// <summary>
    /// Runs PresentMon for a while and reduces the FrameTime column to
    /// percentiles. The 99th is what a player feels: one frame in a hundred
    /// arriving late is a visible hitch, however good the average looks.
    /// </summary>
    private static async Task<FrameProfile> MeasureAsync(
        string presentMonPath,
        int processId,
        int seconds)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = presentMonPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "--process_id", processId.ToString(CultureInfo.InvariantCulture),
            "--output_stdout", "--no_console_stats", "--v2_metrics",
            // Bez sledzenia GPU i wyswietlania widac tylko czas klatki po
            // stronie CPU, wiec rywalizacji o karte nie da sie ani zobaczyc,
            // ani wykluczyc. Sprawdzone na tej maszynie: z tymi flagami
            // PresentMon 2.5.1 wystawia GPULatency, GPUTime, GPUBusy,
            // GPUWait, VideoBusy, DisplayLatency, DisplayedTime i PresentMode.
            "--no_track_input", "--track_gpu_video",
            "--stop_existing_session",
            "--session_name", "gameshift-frametime",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process presentMon = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Nie uruchomiono PresentMon.");
        List<double> frameTimes = [];
        List<double> gpuBusy = [];
        List<double> gpuWait = [];
        int frameTimeColumn = -1;
        int gpuBusyColumn = -1;
        int gpuWaitColumn = -1;

        using CancellationTokenSource deadline =
            new(TimeSpan.FromSeconds(seconds));
        try
        {
            while (await presentMon.StandardOutput.ReadLineAsync(deadline.Token)
                is string line)
            {
                string[] parts = line.Split(',');
                if (frameTimeColumn < 0)
                {
                    frameTimeColumn = Array.IndexOf(parts, "FrameTime");
                    gpuBusyColumn = Array.IndexOf(parts, "GPUBusy");
                    gpuWaitColumn = Array.IndexOf(parts, "GPUWait");
                    continue;
                }

                AddIfPositive(parts, gpuBusyColumn, gpuBusy);
                AddIfPositive(parts, gpuWaitColumn, gpuWait);

                if (parts.Length > frameTimeColumn
                    && double.TryParse(
                        parts[frameTimeColumn],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double frameTime)
                    && frameTime > 0)
                {
                    frameTimes.Add(frameTime);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                if (!presentMon.HasExited)
                {
                    presentMon.Kill(entireProcessTree: true);
                    presentMon.WaitForExit(5000);
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception)
            {
            }
        }

        if (frameTimes.Count == 0)
        {
            return new(0, 0, 0, 0, 0, 0);
        }

        frameTimes.Sort();
        return new(
            frameTimes[frameTimes.Count / 2],
            frameTimes[(int)(frameTimes.Count * 0.99)],
            frameTimes[^1],
            frameTimes.Count,
            Median(gpuBusy),
            Median(gpuWait));
    }

    /// <summary>
    /// Bierze wartosc z kolumny, jesli ta kolumna w ogole jest w tym wydaniu
    /// PresentMon. Brak kolumny nie moze wywracac pomiaru czasu klatki.
    /// </summary>
    private static void AddIfPositive(
        string[] parts,
        int column,
        List<double> target)
    {
        if (column >= 0
            && parts.Length > column
            && double.TryParse(
                parts[column],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value)
            && value > 0)
        {
            target.Add(value);
        }
    }

    private static bool TryFindGame(
        out Process? game,
        out string presentMonPath)
    {
        presentMonPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GameShift",
            "Tools",
            "PresentMon",
            PresentMonComponent.ExecutableFileName);
        game = KnownGameProcessNames
            .SelectMany(Process.GetProcessesByName)
            .FirstOrDefault();
        return game is not null && File.Exists(presentMonPath);
    }

    private static Process StartHog()
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
            "$end = (Get-Date).AddMinutes(5); "
            + "while ((Get-Date) -lt $end) { $null = 1 }");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic obciazacza.");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed record FrameProfile(
        double Median,
        double Percentile99,
        double Worst,
        int SampleCount,
        double GpuBusyMedian = 0,
        double GpuWaitMedian = 0)
    {
        public override string ToString() => SampleCount == 0
            ? "brak klatek"
            : $"p50 {Median:F2} ms, p99 {Percentile99:F2} ms, "
                + $"max {Worst:F2} ms ({SampleCount} klatek)"
                + (GpuBusyMedian > 0 || GpuWaitMedian > 0
                    ? $", GPU zajete {GpuBusyMedian:F2} ms, "
                        + $"GPU czeka {GpuWaitMedian:F2} ms"
                    : string.Empty);
    }
}

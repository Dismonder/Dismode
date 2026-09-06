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
    private static readonly string[] KnownGameProcessNames =
    [
        "RobloxPlayerBeta",
        "re9",
        "MonsterHunterWilds",
        "Cyberpunk2077",
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
    [Timeout(600_000)]
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
        TestContext.WriteLine(
            $"Gra: {game.ProcessName} (PID {game.Id})");

        FrameProfile idle = await MeasureAsync(presentMonPath, game.Id, 12);
        TestContext.WriteLine($"bez obciazenia:   {idle}");

        int hogs = Math.Max(2, Environment.ProcessorCount);
        for (int index = 0; index < hogs; index++)
        {
            _load.Add(StartHog());
        }

        await Task.Delay(3000);
        FrameProfile contended =
            await MeasureAsync(presentMonPath, game.Id, 12);
        TestContext.WriteLine(
            $"pod obciazeniem:  {contended}   ({hogs} obciazaczy)");

        PriorityProBalanceActuator actuator = new();
        ProBalanceSupervisor supervisor = new(
            new CpuProcessSampler(),
            actuator,
            () => new HashSet<int> { game.Id },
            settings: new ProBalanceSettings
            {
                SystemLoadPercent = 0,
                MinimumRestraint = TimeSpan.FromSeconds(1),
                Cooldown = TimeSpan.FromSeconds(1),
                MaximumRestrained = hogs,
            });

        FrameProfile restrained;
        try
        {
            List<string> touched = [];
            for (int index = 0; index < 6; index++)
            {
                foreach (ProBalanceDecision decision in
                    await supervisor.TickAsync(CancellationToken.None))
                {
                    touched.Add(
                        $"{decision.Action} {decision.ProcessName}");
                }

                await Task.Delay(400);
            }

            TestContext.WriteLine(
                "ograniczone: " + string.Join(", ", touched));
            restrained = await MeasureAsync(presentMonPath, game.Id, 12);
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        TestContext.WriteLine($"z ograniczaniem:  {restrained}");
        TestContext.WriteLine(
            "zmiana p99 wzgledem obciazenia: "
            + $"{restrained.Percentile99 - contended.Percentile99:F2} ms");

        Assert.IsGreaterThan(0, idle.SampleCount, "Brak klatek bez obciazenia.");
        Assert.IsGreaterThan(0, contended.SampleCount, "Brak klatek pod obciazeniem.");
        Assert.IsGreaterThan(0, restrained.SampleCount, "Brak klatek z ograniczaniem.");

        // Pomiar na zywej grze szumi, wiec progiem jest wyrazne
        // pogorszenie. Chodzi o wychwycenie regresji, nie o dowodzenie
        // poprawy — te pokazuja liczby powyzej.
        Assert.IsLessThan(
            contended.Percentile99 * 1.5,
            restrained.Percentile99,
            "Ograniczanie pogorszylo ogon czasow klatek.");
    }

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
            "--no_track_input", "--stop_existing_session",
            "--session_name", "gameshift-frametime",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process presentMon = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Nie uruchomiono PresentMon.");
        List<double> frameTimes = [];
        int frameTimeColumn = -1;

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
                    continue;
                }

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
            return new(0, 0, 0, 0);
        }

        frameTimes.Sort();
        return new(
            frameTimes[frameTimes.Count / 2],
            frameTimes[(int)(frameTimes.Count * 0.99)],
            frameTimes[^1],
            frameTimes.Count);
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
        int SampleCount)
    {
        public override string ToString() => SampleCount == 0
            ? "brak klatek"
            : $"p50 {Median:F2} ms, p99 {Percentile99:F2} ms, "
                + $"max {Worst:F2} ms ({SampleCount} klatek)";
    }
}

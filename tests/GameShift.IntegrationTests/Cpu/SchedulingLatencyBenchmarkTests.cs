using System.Diagnostics;
using GameShift.Core.Cpu;
using GameShift.Windows.Cpu;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests.Cpu;

/// <summary>
/// Measures the thing ProBalance is supposed to buy, on this machine.
/// <para>
/// A game's smoothness is not average throughput — it is whether the render
/// thread wakes when it asked to. So the metric here is wake latency: a loop
/// asks to be woken every 16 ms and we record how late it actually was. Under
/// CPU contention the tail of that distribution is what a player feels as
/// stutter, which is why the 99th percentile matters more than the median.
/// </para>
/// <para>
/// This is deliberately a measurement rather than a pass/fail: the numbers are
/// written to the test output. The only assertion is that restraint does not
/// make the tail worse, because a change that costs frame time while claiming
/// to protect it is the one outcome that must never ship quietly.
/// </para>
/// </summary>
[TestClass]
public sealed class SchedulingLatencyBenchmarkTests
{
    private const int TargetIntervalMilliseconds = 16;
    private const int SampleCount = 240;

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
    [Timeout(300_000)]
    public async Task RestraintDoesNotWorsenWakeLatencyUnderContention()
    {
        // Tyle obciazaczy, ile watkow: kazdy chce caly rdzen, wiec petla
        // "renderujaca" musi sie o czas procesora bic.
        int hogs = Math.Max(2, Environment.ProcessorCount);
        for (int index = 0; index < hogs; index++)
        {
            _load.Add(StartHog());
        }

        await Task.Delay(2000);

        LatencyProfile withoutRestraint = await MeasureAsync();

        PriorityProBalanceActuator actuator = new();
        ProBalanceSupervisor supervisor = new(
            new ProcessInventory(),
            actuator,
            static () => new HashSet<int> { Environment.ProcessId },
            settings: new ProBalanceSettings
            {
                SystemLoadPercent = 0,
                MinimumRestraint = TimeSpan.FromSeconds(1),
                Cooldown = TimeSpan.FromSeconds(1),
                MaximumRestrained = hogs,
            });

        LatencyProfile withRestraint;
        try
        {
            // Kilka przejsc, zeby silnik zdazyl zebrac probki i zadzialac.
            for (int index = 0; index < 6; index++)
            {
                await supervisor.TickAsync(CancellationToken.None);
                await Task.Delay(400);
            }

            withRestraint = await MeasureAsync();
        }
        finally
        {
            await supervisor.DisposeAsync();
        }

        TestContext.WriteLine(
            $"Obciazaczy: {hogs}, watkow logicznych: "
            + $"{Environment.ProcessorCount}");
        TestContext.WriteLine(
            "bez ograniczania: " + withoutRestraint);
        TestContext.WriteLine(
            "z ograniczaniem:  " + withRestraint);
        TestContext.WriteLine(
            "zmiana p99: "
            + $"{withRestraint.Percentile99 - withoutRestraint.Percentile99:F2} ms");

        // Pomiar na maszynie ogolnego przeznaczenia szumi, wiec progiem jest
        // wyrazne pogorszenie, nie kazde wahniecie. Chodzi o wychwycenie
        // regresji, nie o udowodnienie poprawy.
        Assert.IsLessThan(
            Math.Max(withoutRestraint.Percentile99 * 2, 40),
            withRestraint.Percentile99,
            "Ograniczanie procesow tla pogorszylo ogon opoznien.");
    }

    /// <summary>
    /// Asks to be woken every 16 ms and records how late each wake was.
    /// Timestamps come from Stopwatch, which is monotonic — the wall clock can
    /// step and would show as a phantom stall.
    /// </summary>
    private static async Task<LatencyProfile> MeasureAsync()
    {
        List<double> lateness = new(SampleCount);
        Stopwatch clock = Stopwatch.StartNew();
        double expected = 0;

        for (int index = 0; index < SampleCount; index++)
        {
            expected += TargetIntervalMilliseconds;
            await Task.Delay(TargetIntervalMilliseconds);
            lateness.Add(Math.Max(0, clock.Elapsed.TotalMilliseconds - expected));
            expected = clock.Elapsed.TotalMilliseconds;
        }

        lateness.Sort();
        return new(
            lateness[lateness.Count / 2],
            lateness[(int)(lateness.Count * 0.99)],
            lateness[^1]);
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
            "$end = (Get-Date).AddMinutes(4); "
            + "while ((Get-Date) -lt $end) { $null = 1 }");
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic obciazacza.");
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed record LatencyProfile(
        double Median,
        double Percentile99,
        double Worst)
    {
        public override string ToString() =>
            $"p50 {Median:F2} ms, p99 {Percentile99:F2} ms, "
            + $"max {Worst:F2} ms";
    }
}

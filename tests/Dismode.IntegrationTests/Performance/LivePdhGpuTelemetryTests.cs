using System.Diagnostics;
using Dismode.Windows.Gpu;

namespace Dismode.IntegrationTests.Performance;

[TestClass]
[TestCategory("Live")]
[DoNotParallelize]
public sealed class LivePdhGpuTelemetryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RealPdhFirstAndTwentySteadyStateSamples()
    {
        await Task.Run(async () =>
        {
            using PdhGpuCounterSource source = new();
            long started = Stopwatch.GetTimestamp();
            if (!TryRead(source, out int engineCount))
            {
                Assert.Inconclusive("PDH GPU Engine counters are unavailable.");
            }

            double first = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            TestContext.WriteLine($"Real PDH first open + collect + read: {first:F6} ms; GPU Engine instances: {engineCount}");
            double total = 0;
            double minimum = double.MaxValue;
            double maximum = 0;
            for (int index = 0; index < 20; index++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.CancellationTokenSource.Token);
                started = Stopwatch.GetTimestamp();
                if (!TryRead(source, out engineCount))
                {
                    Assert.Inconclusive("PDH GPU counters became unavailable during measurement.");
                }

                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                total += elapsed;
                minimum = Math.Min(minimum, elapsed);
                maximum = Math.Max(maximum, elapsed);
                TestContext.WriteLine($"Real PDH steady sample {index + 1}: {elapsed:F6} ms; GPU Engine instances: {engineCount}");
            }

            TestContext.WriteLine($"Real PDH steady N=20 at 1 Hz: mean {total / 20:F6} ms/sample; min {minimum:F6}; max {maximum:F6}");
        }, TestContext.CancellationTokenSource.Token);
    }

    private static bool TryRead(PdhGpuCounterSource source, out int engineCount)
    {
        engineCount = 0;
        try
        {
            if (!source.TryRead(out ReadOnlySpan<GpuCounterReading> readings))
            {
                return false;
            }

            foreach (GpuCounterReading reading in readings)
            {
                if (reading.Valid && reading.Identity.Kind == GpuCounterKind.Engine)
                {
                    engineCount++;
                }
            }

            return engineCount > 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}

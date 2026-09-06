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
    public void ProcessInventoryCaptureIsFastEnoughToPoll()
    {
        ProcessInventory inventory = new();
        // Pierwsze przejscie rozgrzewa cache systemu; mierzymy nastepne.
        _ = inventory.Capture();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<ProcessSnapshot> snapshot = inventory.Capture();
        stopwatch.Stop();

        Assert.IsGreaterThan(0, snapshot.Count);
        Assert.IsLessThan(
            1500,
            stopwatch.ElapsedMilliseconds,
            $"Inwentaryzacja {snapshot.Count} procesow zajela "
                + $"{stopwatch.ElapsedMilliseconds} ms, a petla ma chodzic co "
                + "2 s.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task OneSupervisorPassFitsInsideItsInterval()
    {
        await using ProBalanceSupervisor supervisor = new(
            new ProcessInventory(),
            new CountingActuator(),
            static () => new HashSet<int>(),
            settings: new ProBalanceSettings { SystemLoadPercent = 200 });

        await supervisor.TickAsync(CancellationToken.None);

        Stopwatch stopwatch = Stopwatch.StartNew();
        await supervisor.TickAsync(CancellationToken.None);
        stopwatch.Stop();

        Assert.IsLessThan(
            2000,
            stopwatch.ElapsedMilliseconds,
            $"Jedno przejscie zajelo {stopwatch.ElapsedMilliseconds} ms.");
    }

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

using Dismode.Core.Cpu;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.Cpu;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Cpu;

[TestClass]
public sealed class ProBalanceSupervisorTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 9, 6, 11, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task BusyBackgroundProcessIsRestrainedAndThenReleased()
    {
        AdvancingTimeProvider time = new(
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        StubInventory inventory = new();
        RecordingActuator actuator = new();
        await using ProBalanceSupervisor supervisor = new(
            inventory,
            actuator,
            static () => new HashSet<int> { 1001 },
            timeProvider: time);

        // Pierwszy tick tylko ustala punkt odniesienia: bez poprzedniego
        // odczytu nie da sie policzyc zuzycia.
        inventory.Set(cpuMilliseconds: 0);
        await supervisor.TickAsync(CancellationToken.None);

        for (int index = 0; index < 4; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            // 1600 ms procesora na 2000 ms sciany to 0,8 rdzenia,
            // czyli powyzej progu 0,75.
            inventory.Advance(1600);
            await supervisor.TickAsync(CancellationToken.None);
        }

        CollectionAssert.Contains(
            actuator.Restrained,
            new ProcessRuntimeKey(4242, Started));
        Assert.AreEqual(
            0,
            actuator.Released.Count,
            "Nic jeszcze nie powinno zostac zwolnione.");

        // Proces cichnie: zero czasu procesora przez kolejne probki.
        for (int index = 0; index < 8; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            inventory.Advance(0);
            await supervisor.TickAsync(CancellationToken.None);
        }

        CollectionAssert.Contains(
            actuator.Released,
            new ProcessRuntimeKey(4242, Started));
    }

    [TestMethod]
    public async Task GameAndShellAreNeverTouched()
    {
        AdvancingTimeProvider time = new(
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        StubInventory inventory = new(includeGameAndShell: true);
        RecordingActuator actuator = new();
        await using ProBalanceSupervisor supervisor = new(
            inventory,
            actuator,
            static () => new HashSet<int> { 1001 },
            timeProvider: time);

        inventory.Set(cpuMilliseconds: 0);
        await supervisor.TickAsync(CancellationToken.None);
        for (int index = 0; index < 10; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            inventory.Advance(1800);
            await supervisor.TickAsync(CancellationToken.None);
        }

        Assert.IsFalse(
            actuator.Restrained.Any(key => key.ProcessId is 1001 or 7),
            "Gra albo powloka zostala ograniczona.");
    }

    [TestMethod]
    public async Task StoppingReleasesWhatIsStillHeld()
    {
        AdvancingTimeProvider time = new(
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        StubInventory inventory = new();
        RecordingActuator actuator = new();
        ProBalanceSupervisor supervisor = new(
            inventory,
            actuator,
            static () => new HashSet<int>(),
            timeProvider: time);

        inventory.Set(cpuMilliseconds: 0);
        await supervisor.TickAsync(CancellationToken.None);
        for (int index = 0; index < 4; index++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            inventory.Advance(1600);
            await supervisor.TickAsync(CancellationToken.None);
        }

        Assert.AreEqual(1, actuator.Restrained.Count);

        await supervisor.DisposeAsync();

        CollectionAssert.Contains(
            actuator.Released,
            new ProcessRuntimeKey(4242, Started));
    }

    [TestMethod]
    public async Task ConcurrentTicksDoNotCorruptState()
    {
        // Petla i wywolanie reczne dziela slownik poprzednich odczytow oraz
        // stan silnika. Bez bramki rownolegly przebieg uszkadza oba.
        AdvancingTimeProvider time = new(
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        StubInventory inventory = new();
        RecordingActuator actuator = new();
        await using ProBalanceSupervisor supervisor = new(
            inventory,
            actuator,
            static () => new HashSet<int>(),
            timeProvider: time);

        inventory.Set(cpuMilliseconds: 0);
        await supervisor.TickAsync(CancellationToken.None);

        for (int round = 0; round < 6; round++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            inventory.Advance(1600);
            await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ =>
                    supervisor.TickAsync(CancellationToken.None).AsTask()));
        }

        // Bramka nie gwarantuje jednego ograniczenia — kazdy tick przesuwa
        // licznik probek — ale gwarantuje, ze nic sie nie wysypie i ze proces
        // nie zostanie ograniczony wielokrotnie bez zwolnienia miedzy tym.
        Assert.IsTrue(
            actuator.Restrained.Count
                <= actuator.Released.Count + 1,
            $"Ograniczen {actuator.Restrained.Count} przy "
                + $"{actuator.Released.Count} zwolnieniach.");
    }

    [TestMethod]
    public async Task RefusedRestraintDoesNotKeepHoldingASlot()
    {
        AdvancingTimeProvider time = new(
            new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        StubInventory inventory = new();
        RecordingActuator actuator = new() { Accept = false };
        await using ProBalanceSupervisor supervisor = new(
            inventory,
            actuator,
            static () => new HashSet<int>(),
            timeProvider: time);

        inventory.Set(cpuMilliseconds: 0);
        await supervisor.TickAsync(CancellationToken.None);

        int ticks = 0;
        for (; ticks < 8 && actuator.Restrained.Count == 0; ticks++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            inventory.Advance(1600);
            await supervisor.TickAsync(CancellationToken.None);
        }

        Assert.AreEqual(1, actuator.Restrained.Count);

        // A refused restraint must not stay in the engine's count of
        // restrained processes, otherwise it eats one of the limited slots.
        time.Advance(TimeSpan.FromSeconds(2));
        inventory.Advance(1600);
        IReadOnlyList<ProBalanceDecision> decisions =
            await supervisor.TickAsync(CancellationToken.None);
        Assert.AreEqual(0, decisions.Count);
        Assert.AreEqual(
            1,
            actuator.Restrained.Count,
            "Odrzucone ograniczenie nie moze byc ponawiane w czasie cooldownu.");
    }

    [TestMethod]
    public void AbandonedRestraintFreesTheSlotAndStartsCooldown()
    {
        ProBalanceEngine engine = new(new ProBalanceSettings
        {
            MaximumRestrained = 1,
            SustainedSamples = 1,
        });
        DateTimeOffset now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        ProcessRuntimeKey first = new(1, Started);
        ProcessRuntimeKey second = new(2, Started);

        IReadOnlyList<ProBalanceDecision> decisions = engine.Evaluate(
            [new(first, "a", 1.0, false, false)],
            2.0,
            now);
        Assert.AreEqual(ProBalanceAction.Restrain, decisions[0].Action);

        // The slot is taken, so a second hog is refused.
        decisions = engine.Evaluate(
            [
                new(first, "a", 1.0, false, false),
                new(second, "b", 0.9, false, false),
            ],
            2.0,
            now);
        Assert.AreEqual(0, decisions.Count);

        engine.AbandonRestraint(first, now);
        Assert.AreEqual(0, engine.Restrained.Count);

        decisions = engine.Evaluate(
            [
                new(first, "a", 1.0, false, false),
                new(second, "b", 0.9, false, false),
            ],
            2.0,
            now);
        Assert.AreEqual(1, decisions.Count);
        Assert.AreEqual(second, decisions[0].RuntimeKey);
    }

    private sealed class StubInventory(bool includeGameAndShell = false)
        : ICpuProcessSource
    {
        private long _hogMilliseconds;
        private long _otherMilliseconds;

        public void Set(long cpuMilliseconds)
        {
            _hogMilliseconds = cpuMilliseconds;
            _otherMilliseconds = cpuMilliseconds;
        }

        public void Advance(long milliseconds)
        {
            _hogMilliseconds += milliseconds;
            _otherMilliseconds += milliseconds;
        }

        public IReadOnlyList<CpuProcessSample> Capture()
        {
            List<CpuProcessSample> samples =
            [
                new(
                    4242,
                    "indexer",
                    Started,
                    TimeSpan.FromMilliseconds(_hogMilliseconds)),
            ];

            if (includeGameAndShell)
            {
                samples.Add(new(
                    1001,
                    "re9",
                    Started,
                    TimeSpan.FromMilliseconds(_otherMilliseconds)));
                samples.Add(new(
                    7,
                    "dwm",
                    Started,
                    TimeSpan.FromMilliseconds(_otherMilliseconds)));
            }

            return samples;
        }
    }

    /// <summary>
    /// Time the test moves by hand, so the engine's minimum hold and cooldown
    /// can be crossed without the test waiting through them.
    /// </summary>
    private sealed class AdvancingTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private DateTimeOffset _now = utcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class RecordingActuator : IProBalanceActuator
    {
        public bool Accept { get; init; } = true;

        public List<ProcessRuntimeKey> Restrained { get; } = [];

        public List<ProcessRuntimeKey> Released { get; } = [];

        public ValueTask<bool> RestrainAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            Restrained.Add(runtimeKey);
            return ValueTask.FromResult(Accept);
        }

        public ValueTask<bool> ReleaseAsync(
            ProcessRuntimeKey runtimeKey,
            CancellationToken cancellationToken)
        {
            Released.Add(runtimeKey);
            return ValueTask.FromResult(true);
        }
    }
}

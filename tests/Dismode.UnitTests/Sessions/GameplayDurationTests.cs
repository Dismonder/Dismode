using Dismode.Core.Sessions;

namespace Dismode.UnitTests.Sessions;

[TestClass]
public sealed class GameplayDurationTests
{
    [TestMethod]
    public void ExitRollsBackPollDelayToLastAliveAwakeObservation()
    {
        Clock clock = new();
        GameplayDuration duration = new(clock);
        clock.Advance(10, 10);
        duration.ObserveAlive();
        clock.Advance(4, 3600);
        Assert.AreEqual(14L, duration.Seconds);
        duration.PauseAtLastAlive();
        Assert.AreEqual(10L, duration.Seconds);
        Assert.AreEqual(10L, duration.CheckpointSeconds);
        clock.Advance(30, 30);
        Assert.AreEqual(10L, duration.Seconds);
    }

    [TestMethod]
    public void SlowGateDoesNotMoveTheRecordedAliveObservation()
    {
        Clock clock = new();
        GameplayDuration duration = new(clock);
        clock.Advance(10, 10);
        long observed = clock.GetTimestamp();
        clock.Advance(5, 5);
        duration.ObserveAlive(observed);
        duration.PauseAtLastAlive();
        Assert.AreEqual(10L, duration.Seconds);
    }

    [TestMethod]
    public void SleepAndUtcJumpsDoNotIncreasePlayTime()
    {
        Clock clock = new();
        GameplayDuration duration = new(clock);
        clock.Advance(20, 20);
        Assert.AreEqual(20L, duration.Seconds);
        clock.Advance(0, 3600);
        Assert.AreEqual(20L, duration.Seconds);
        clock.Advance(10, -7200);
        Assert.AreEqual(30L, duration.Seconds);
    }

    [TestMethod]
    public void ExitFreezesDurationBeforeSlowRestoreAndCheckpointBoundsCrash()
    {
        Clock clock = new();
        GameplayDuration duration = new(clock);
        clock.Advance(42, 42);
        long checkpoint = duration.Seconds;
        duration.Pause();
        clock.Advance(300, 300);
        Assert.AreEqual(42L, duration.Seconds);
        GameplayDuration recovered = new(clock, checkpoint);
        Assert.AreEqual(42L, recovered.Seconds);
        clock.Advance(8, 8);
        Assert.AreEqual(50L, recovered.Seconds);
    }

    [TestMethod]
    public void ManyShortSessionsAreSummedAsSeconds()
    {
        Clock clock = new();
        long sum = 0;
        for (int index = 0; index < 100; index++)
        {
            GameplayDuration duration = new(clock);
            clock.Advance(12, 12);
            sum += duration.Seconds;
        }
        Assert.AreEqual(1200L, sum);
    }

    private sealed class Clock : TimeProvider
    {
        private long _awake;
        private long _wall;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _awake;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(_wall);
        internal void Advance(int awake, int wall) { _awake += awake; _wall += wall; }
    }
}

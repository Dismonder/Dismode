namespace Dismode.Core.Sessions;

// Zegar musi mierzyc czas aktywnego systemu, bez uspienia.
public sealed class GameplayDuration
{
    private readonly TimeProvider _clock;
    private readonly object _sync = new();
    private long _lastTimestamp;
    private TimeSpan _elapsed;
    private TimeSpan _lastAliveElapsed;
    private bool _paused;

    public GameplayDuration(TimeProvider clock, long checkpointSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointSeconds);
        _clock = clock;
        _lastTimestamp = clock.GetTimestamp();
        _elapsed = TimeSpan.FromSeconds(checkpointSeconds);
        _lastAliveElapsed = _elapsed;
    }

    public long CheckpointSeconds { get { lock (_sync) { return (long)_lastAliveElapsed.TotalSeconds; } } }

    public long Seconds
    {
        get { lock (_sync) { Update(); return (long)_elapsed.TotalSeconds; } }
    }

    public void Pause() { lock (_sync) { Update(); _paused = true; _lastAliveElapsed = _elapsed; } }

    public void ObserveAlive(long? observedAt = null)
    {
        lock (_sync)
        {
            long timestamp = observedAt ?? _clock.GetTimestamp();
            bool wasPaused = _paused;
            Update();
            if (wasPaused)
            {
                _lastTimestamp = timestamp;
                _lastAliveElapsed = _elapsed;
                _paused = false;
            }
            else
            {
                TimeSpan sinceObservation = _clock.GetElapsedTime(timestamp, _lastTimestamp);
                _lastAliveElapsed = _elapsed - (sinceObservation > TimeSpan.Zero ? sinceObservation : TimeSpan.Zero);
                if (_lastAliveElapsed < TimeSpan.Zero) { _lastAliveElapsed = TimeSpan.Zero; }
            }
        }
    }

    public void PauseAtLastAlive()
    {
        lock (_sync)
        {
            _elapsed = _lastAliveElapsed;
            _lastTimestamp = _clock.GetTimestamp();
            _paused = true;
        }
    }

    private void Update()
    {
        long timestamp = _clock.GetTimestamp();
        TimeSpan awake = _clock.GetElapsedTime(_lastTimestamp, timestamp);
        if (!_paused && awake > TimeSpan.Zero)
        {
            _elapsed += awake;
        }
        _lastTimestamp = timestamp;
    }
}

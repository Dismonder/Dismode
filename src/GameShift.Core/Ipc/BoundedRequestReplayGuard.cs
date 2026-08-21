namespace GameShift.Core.Ipc;

public sealed class BoundedRequestReplayGuard : IRequestReplayGuard
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DateTimeOffset> _requests = [];
    private readonly int _capacity;
    private readonly TimeSpan _retention;

    public BoundedRequestReplayGuard(
        int capacity = 4096,
        TimeSpan? retention = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Replay capacity must be positive.");
        }

        TimeSpan effectiveRetention = retention ?? TimeSpan.FromMinutes(5);
        if (effectiveRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention),
                effectiveRetention,
                "Replay retention must be positive.");
        }

        _capacity = capacity;
        _retention = effectiveRetention;
    }

    public ReplayRegistrationResult TryRegister(
        Guid requestId,
        DateTimeOffset observedAtUtc)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A request ID cannot be empty.", nameof(requestId));
        }

        DateTimeOffset nowUtc = observedAtUtc.ToUniversalTime();

        lock (_sync)
        {
            DateTimeOffset oldestAllowed = nowUtc - _retention;
            Guid[] expired = _requests
                .Where(pair => pair.Value < oldestAllowed)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (Guid expiredRequest in expired)
            {
                _requests.Remove(expiredRequest);
            }

            if (_requests.ContainsKey(requestId))
            {
                return ReplayRegistrationResult.Duplicate;
            }

            if (_requests.Count >= _capacity)
            {
                return ReplayRegistrationResult.CapacityExhausted;
            }

            _requests.Add(requestId, nowUtc);
            return ReplayRegistrationResult.Registered;
        }
    }
}


namespace Dismode.UI.Services;

internal sealed class GameDetectionProfileIndex<T>(Func<T, bool> enabled, Func<T, string> executablePath)
{
    private Dictionary<string, List<T>>? _cached;

    internal void Invalidate() => _cached = null;

    internal Dictionary<string, List<T>> Get(IReadOnlyList<T> profiles)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        Dictionary<string, List<T>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (T profile in profiles)
        {
            if (!enabled(profile))
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(executablePath(profile));
            if (!byName.TryGetValue(name, out List<T>? matches))
            {
                matches = [];
                byName[name] = matches;
            }

            matches.Add(profile);
        }

        _cached = byName;
        return byName;
    }
}

internal sealed class GameDetectionScanCadence
{
    private DateTimeOffset? _lastStarted;
    private bool _inFlight;
    private bool _requested;

    internal void Request() => _requested = true;

    internal bool TryBegin(DateTimeOffset now, TimeSpan nominalInterval)
    {
        if (_inFlight || (_lastStarted is DateTimeOffset previous
            && (now - previous < TimeSpan.FromSeconds(1)
                || (!_requested && now - previous < nominalInterval))))
        {
            return false;
        }

        _inFlight = true;
        _requested = false;
        _lastStarted = now;
        return true;
    }

    internal void End() => _inFlight = false;
}

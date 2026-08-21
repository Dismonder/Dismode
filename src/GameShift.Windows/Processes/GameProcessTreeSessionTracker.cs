using System.ComponentModel;
using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public sealed class GameProcessTreeSessionTracker
{
    private const int MaximumTrackedProcesses = 64;
    private const int MaximumCandidateProcesses = 256;

    private readonly ProcessIdentity _root;
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly IProcessParentMapProvider _parentMapProvider;
    private readonly object _stateLock = new();
    private readonly Dictionary<ProcessRuntimeKey, ProcessIdentity> _known = [];
    private readonly HashSet<ProcessRuntimeKey> _consumedMissingSeeds = [];

    public GameProcessTreeSessionTracker(
        ProcessIdentity root,
        IEnumerable<ProcessIdentity>? previouslyTracked = null,
        IProcessIdentityProvider? identityProvider = null,
        IProcessParentMapProvider? parentMapProvider = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _identityProvider =
            identityProvider ?? new ProcessIdentityProvider();
        _parentMapProvider =
            parentMapProvider ?? new ProcessParentMapProvider();

        AddKnown(root);
        if (previouslyTracked is null)
        {
            return;
        }

        foreach (ProcessIdentity identity in previouslyTracked)
        {
            AddKnown(identity);
        }
    }

    public IReadOnlyList<ProcessIdentity> GetKnownProcesses()
    {
        lock (_stateLock)
        {
            return _known.Values
                .OrderBy(identity => identity.RuntimeKey.StartedAtUtc)
                .ThenBy(identity => identity.RuntimeKey.ProcessId)
                .ToArray();
        }
    }

    public async ValueTask<GameProcessTreeObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        ProcessIdentity[] known = GetKnownProcesses().ToArray();
        Dictionary<int, ProcessIdentity> trustedSeeds = [];
        List<ProcessIdentity> running = [];

        foreach (ProcessIdentity identity in known)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool matches = await _identityProvider
                .MatchesRuntimeIdentityAsync(identity, cancellationToken)
                .ConfigureAwait(false);
            if (matches)
            {
                trustedSeeds[identity.RuntimeKey.ProcessId] = identity;
                running.Add(identity);
                continue;
            }

            bool canUseMissingParentOnce;
            lock (_stateLock)
            {
                canUseMissingParentOnce =
                    _consumedMissingSeeds.Add(identity.RuntimeKey);
            }

            if (!canUseMissingParentOnce)
            {
                continue;
            }

            ProcessIdentity? currentAtReusedPid =
                await _identityProvider.TryCaptureAsync(
                        identity.RuntimeKey.ProcessId,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (currentAtReusedPid is null)
            {
                // A Toolhelp snapshot retains the original parent PID for a
                // child after its launcher exits. Use that trusted PID for one
                // observation only, and never after PID reuse is detected.
                trustedSeeds[identity.RuntimeKey.ProcessId] = identity;
            }
        }

        IReadOnlyDictionary<int, int> parents;
        try
        {
            parents = _parentMapProvider.Capture();
        }
        catch (Exception exception) when (
            exception is
                Win32Exception
                or InvalidOperationException
                or UnauthorizedAccessException)
        {
            return new(
                GetKnownProcesses(),
                running.ToArray(),
                NewlyDiscoveredCount: 0,
                IsReliable: false);
        }

        Dictionary<int, List<int>> childrenByParent = [];
        foreach ((int processId, int parentProcessId) in parents)
        {
            if (processId <= 0
                || parentProcessId <= 0
                || processId == parentProcessId)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(
                    parentProcessId,
                    out List<int>? children))
            {
                children = [];
                childrenByParent[parentProcessId] = children;
            }

            children.Add(processId);
        }

        Queue<(int ProcessId, int ParentProcessId)> pending = new();
        HashSet<int> visited = [.. trustedSeeds.Keys];
        foreach (int seedProcessId in trustedSeeds.Keys)
        {
            EnqueueChildren(
                seedProcessId,
                childrenByParent,
                visited,
                pending);
        }

        Dictionary<int, ProcessIdentity> acceptedByProcessId =
            new(trustedSeeds);
        List<ProcessIdentity> newlyDiscovered = [];
        int examinedCandidateCount = 0;

        while (pending.TryDequeue(
                   out (int ProcessId, int ParentProcessId) candidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            examinedCandidateCount++;
            if (examinedCandidateCount > MaximumCandidateProcesses
                || GetKnownProcessCount() >= MaximumTrackedProcesses)
            {
                return new(
                    GetKnownProcesses(),
                    running.ToArray(),
                    newlyDiscovered.Count,
                    IsReliable: false);
            }

            if (!acceptedByProcessId.TryGetValue(
                    candidate.ParentProcessId,
                    out ProcessIdentity? parentIdentity))
            {
                continue;
            }

            ProcessIdentity? childIdentity =
                await _identityProvider.TryCaptureAsync(
                        candidate.ProcessId,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (childIdentity is null
                || childIdentity.SessionId != _root.SessionId
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    childIdentity.UserSid,
                    _root.UserSid)
                || childIdentity.RuntimeKey.StartedAtUtc
                    < _root.RuntimeKey.StartedAtUtc
                || childIdentity.RuntimeKey.StartedAtUtc
                    < parentIdentity.RuntimeKey.StartedAtUtc)
            {
                continue;
            }

            acceptedByProcessId[candidate.ProcessId] = childIdentity;
            if (AddKnown(childIdentity))
            {
                newlyDiscovered.Add(childIdentity);
            }

            if (!running.Any(identity =>
                    identity.RuntimeKey == childIdentity.RuntimeKey))
            {
                running.Add(childIdentity);
            }

            EnqueueChildren(
                candidate.ProcessId,
                childrenByParent,
                visited,
                pending);
        }

        return new(
            GetKnownProcesses(),
            running
                .OrderBy(identity => identity.RuntimeKey.StartedAtUtc)
                .ThenBy(identity => identity.RuntimeKey.ProcessId)
                .ToArray(),
            newlyDiscovered.Count,
            IsReliable: true);
    }

    private bool AddKnown(ProcessIdentity identity)
    {
        if (identity.SessionId != _root.SessionId
            || !StringComparer.OrdinalIgnoreCase.Equals(
                identity.UserSid,
                _root.UserSid)
            || identity.RuntimeKey.StartedAtUtc
                < _root.RuntimeKey.StartedAtUtc)
        {
            throw new ArgumentException(
                "A tracked game process must belong to the same user "
                + "session and cannot predate the approved root process.",
                nameof(identity));
        }

        lock (_stateLock)
        {
            if (_known.ContainsKey(identity.RuntimeKey))
            {
                return false;
            }

            if (_known.Count >= MaximumTrackedProcesses)
            {
                throw new InvalidOperationException(
                    "The game process tree exceeded the safe tracking limit.");
            }

            _known.Add(identity.RuntimeKey, identity);
            return true;
        }
    }

    private int GetKnownProcessCount()
    {
        lock (_stateLock)
        {
            return _known.Count;
        }
    }

    private static void EnqueueChildren(
        int parentProcessId,
        Dictionary<int, List<int>> childrenByParent,
        HashSet<int> visited,
        Queue<(int ProcessId, int ParentProcessId)> pending)
    {
        if (!childrenByParent.TryGetValue(
                parentProcessId,
                out List<int>? children))
        {
            return;
        }

        foreach (int childProcessId in children)
        {
            if (visited.Add(childProcessId))
            {
                pending.Enqueue((childProcessId, parentProcessId));
            }
        }
    }
}

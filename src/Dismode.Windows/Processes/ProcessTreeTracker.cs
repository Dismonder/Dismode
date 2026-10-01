using Dismode.Core.Domain.Processes;

namespace Dismode.Windows.Processes;

public sealed class ProcessTreeTracker
{
    private readonly IProcessIdentityProvider _identityProvider;
    private readonly IProcessParentMapProvider _parentMapProvider;
    private readonly TimeProvider _timeProvider;

    public ProcessTreeTracker(
        IProcessIdentityProvider? identityProvider = null,
        IProcessParentMapProvider? parentMapProvider = null,
        TimeProvider? timeProvider = null)
    {
        _identityProvider = identityProvider ?? new ProcessIdentityProvider();
        _parentMapProvider =
            parentMapProvider ?? new ProcessParentMapProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<ProcessTreeSnapshot> CaptureAsync(
        ProcessIdentity expectedRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedRoot);
        ProcessIdentity? currentRoot =
            await _identityProvider
                .TryCaptureAsync(
                    expectedRoot.RuntimeKey.ProcessId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (currentRoot is null
            || !expectedRoot.MatchesExecutable(currentRoot))
        {
            throw new InvalidOperationException(
                "The game root process identity changed.");
        }

        IReadOnlyDictionary<int, int> parents = _parentMapProvider.Capture();
        Dictionary<int, int> depths = new()
        {
            [currentRoot.RuntimeKey.ProcessId] = 0,
        };
        Queue<int> pending = new();
        pending.Enqueue(currentRoot.RuntimeKey.ProcessId);

        while (pending.TryDequeue(out int parentId))
        {
            int childDepth = depths[parentId] + 1;
            foreach ((int childId, int observedParentId) in parents)
            {
                if (observedParentId != parentId
                    || depths.ContainsKey(childId))
                {
                    continue;
                }

                depths[childId] = childDepth;
                pending.Enqueue(childId);
            }
        }

        List<ProcessTreeMember> members =
        [
            new(currentRoot, ParentProcessId: null, Depth: 0),
        ];
        foreach ((int processId, int depth) in depths
                     .Where(pair => pair.Value > 0)
                     .OrderBy(pair => pair.Value)
                     .ThenBy(pair => pair.Key))
        {
            ProcessIdentity? identity =
                await _identityProvider
                    .TryCaptureAsync(processId, cancellationToken)
                    .ConfigureAwait(false);
            if (identity is null
                || identity.SessionId != currentRoot.SessionId
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    identity.UserSid,
                    currentRoot.UserSid))
            {
                continue;
            }

            members.Add(
                new(
                    identity,
                    parents.TryGetValue(processId, out int parentId)
                        ? parentId
                        : null,
                    depth));
        }

        return new(
            currentRoot,
            members,
            _timeProvider.GetUtcNow());
    }
}

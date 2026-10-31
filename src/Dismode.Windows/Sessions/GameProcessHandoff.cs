using Dismode.Core.Domain.Processes;
using Dismode.Windows.Processes;

namespace Dismode.Windows.Sessions;

public sealed record GameHandoffIdentity(string ExecutablePath, string ExecutableSha256,
    IReadOnlyList<ProcessRuntimeKey> ActivationBaseline);

public sealed class GameProcessHandoff(DateTimeOffset missingAtUtc, long timestamp)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    public long Timestamp { get; } = timestamp;

    public bool Finished { get; private set; }

    public void Refuse() => Finished = true;

    public ProcessIdentity? Select(ProcessIdentity root,
        IReadOnlyList<ProcessIdentity> known, IEnumerable<ProcessIdentity> candidates,
        TimeSpan elapsed, DateTimeOffset? nowUtc = null, GameHandoffIdentity? approval = null)
    {
        if (Finished || elapsed >= Window || known.Count >= 64)
        {
            Finished = true;
            return null;
        }

        ProcessIdentity[] matches = candidates.Where(candidate =>
                candidate.SessionId == root.SessionId
                && StringComparer.OrdinalIgnoreCase.Equals(candidate.UserSid, root.UserSid)
                && MatchesApprovedExecutable(candidate, root, approval)
                && candidate.RuntimeKey.StartedAtUtc >= root.RuntimeKey.StartedAtUtc
                && candidate.RuntimeKey.StartedAtUtc <= (nowUtc ?? missingAtUtc + elapsed)
                && approval?.ActivationBaseline.Contains(candidate.RuntimeKey) != true
                && !known.Any(previous => previous.RuntimeKey.ProcessId == candidate.RuntimeKey.ProcessId))
            .DistinctBy(candidate => candidate.RuntimeKey)
            .Take(2).ToArray();
        Finished = matches.Length > 0;
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool MatchesApprovedExecutable(ProcessIdentity candidate, ProcessIdentity root,
        GameHandoffIdentity? approval) =>
        ExecutablePathIdentity.AreSameExecutable(candidate.ExecutablePath, root.ExecutablePath)
            && StringComparer.OrdinalIgnoreCase.Equals(candidate.ExecutableSha256, root.ExecutableSha256)
        || approval is not null
            && !ExecutablePathIdentity.AreSameExecutable(approval.ExecutablePath, root.ExecutablePath)
            && ExecutablePathIdentity.AreSameExecutable(candidate.ExecutablePath, approval.ExecutablePath)
            && StringComparer.OrdinalIgnoreCase.Equals(candidate.ExecutableSha256, approval.ExecutableSha256);
}

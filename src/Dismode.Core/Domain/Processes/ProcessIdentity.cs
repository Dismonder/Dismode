namespace Dismode.Core.Domain.Processes;

public sealed record ProcessIdentity
{
    public ProcessIdentity(
        ProcessRuntimeKey runtimeKey,
        string executablePath,
        string executableSha256,
        string? publisher,
        string userSid,
        int sessionId,
        ProcessRuntimeKey? parentRuntimeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "An executable path must be fully qualified.",
                nameof(executablePath));
        }

        if (!IsSha256(executableSha256))
        {
            throw new ArgumentException(
                "An executable hash must be a 64-character SHA-256 value.",
                nameof(executableSha256));
        }

        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sessionId),
                sessionId,
                "A Windows session ID cannot be negative.");
        }

        RuntimeKey = runtimeKey;
        ExecutablePath = executablePath;
        ExecutableSha256 = executableSha256.ToUpperInvariant();
        Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim();
        UserSid = userSid.Trim();
        SessionId = sessionId;
        ParentRuntimeKey = parentRuntimeKey;
    }

    public ProcessRuntimeKey RuntimeKey { get; }

    public string ExecutablePath { get; }

    public string ExecutableSha256 { get; }

    public string? Publisher { get; }

    public string UserSid { get; }

    public int SessionId { get; }

    public ProcessRuntimeKey? ParentRuntimeKey { get; }

    public bool MatchesRuntimeIdentity(ProcessIdentity candidate) =>
        RuntimeKey == candidate.RuntimeKey
        && SessionId == candidate.SessionId
        && StringComparer.OrdinalIgnoreCase.Equals(ExecutablePath, candidate.ExecutablePath)
        && StringComparer.OrdinalIgnoreCase.Equals(UserSid, candidate.UserSid);

    public bool MatchesExecutable(ProcessIdentity candidate) =>
        MatchesRuntimeIdentity(candidate)
        && StringComparer.OrdinalIgnoreCase.Equals(ExecutableSha256, candidate.ExecutableSha256);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}


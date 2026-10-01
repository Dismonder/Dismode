using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Services;

public sealed record ServiceStopApproval
{
    private static readonly TimeSpan MaximumApprovalLifetime =
        TimeSpan.FromHours(1);

    public ServiceStopApproval(
        string serviceName,
        SessionId sessionId,
        string approvedBySid,
        DateTimeOffset approvedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBySid);

        DateTimeOffset approved = approvedAtUtc.ToUniversalTime();
        DateTimeOffset expires = expiresAtUtc.ToUniversalTime();
        if (expires <= approved
            || expires - approved > MaximumApprovalLifetime)
        {
            throw new ArgumentException(
                "A service approval must expire within one hour.",
                nameof(expiresAtUtc));
        }

        ServiceName = serviceName.Trim();
        SessionId = sessionId;
        ApprovedBySid = approvedBySid.Trim();
        ApprovedAtUtc = approved;
        ExpiresAtUtc = expires;
    }

    public string ServiceName { get; }

    public SessionId SessionId { get; }

    public string ApprovedBySid { get; }

    public DateTimeOffset ApprovedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsValidFor(
        string serviceName,
        SessionId sessionId,
        DateTimeOffset nowUtc) =>
        StringComparer.OrdinalIgnoreCase.Equals(ServiceName, serviceName)
        && SessionId == sessionId
        && nowUtc.ToUniversalTime() >= ApprovedAtUtc
        && nowUtc.ToUniversalTime() <= ExpiresAtUtc;
}

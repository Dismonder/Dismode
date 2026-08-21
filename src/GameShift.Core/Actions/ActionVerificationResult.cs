namespace GameShift.Core.Actions;

public sealed record ActionVerificationResult(bool MatchesExpectedState, string? Details)
{
    public static ActionVerificationResult Verified() => new(true, null);

    public static ActionVerificationResult Failed(string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        return new(false, details.Trim());
    }
}


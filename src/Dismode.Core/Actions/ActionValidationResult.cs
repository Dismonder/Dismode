namespace Dismode.Core.Actions;

public sealed record ActionValidationResult(bool IsValid, string? BlockingReason)
{
    public static ActionValidationResult Allowed() => new(true, null);

    public static ActionValidationResult Blocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(false, reason.Trim());
    }
}


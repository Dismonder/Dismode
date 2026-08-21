namespace GameShift.Core.Ipc;

public sealed record RequestValidationResult(
    bool IsValid,
    RequestValidationFailure Failure,
    string? Details)
{
    public static RequestValidationResult Valid() =>
        new(true, RequestValidationFailure.None, null);

    public static RequestValidationResult Rejected(
        RequestValidationFailure failure,
        string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details);
        return new(false, failure, details.Trim());
    }
}


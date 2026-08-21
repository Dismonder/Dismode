namespace GameShift.Core.Updates;

public sealed record UpdatePreferences(
    bool AutomaticChecksEnabled,
    bool AutomaticInstallEnabled,
    string Channel,
    DateTimeOffset? LastSuccessfulCheckAtUtc,
    string? LastObservedVersion,
    string? LastError,
    DateTimeOffset UpdatedAtUtc)
{
    public static readonly TimeSpan AutomaticCheckInterval =
        TimeSpan.FromDays(30);

    public static UpdatePreferences CreateDefault() =>
        new(
            AutomaticChecksEnabled: true,
            AutomaticInstallEnabled: true,
            Channel: "preview",
            LastSuccessfulCheckAtUtc: null,
            LastObservedVersion: null,
            LastError: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow);

    public bool IsAutomaticCheckDue(DateTimeOffset nowUtc) =>
        AutomaticChecksEnabled
        && (LastSuccessfulCheckAtUtc is null
            || nowUtc.ToUniversalTime()
                - LastSuccessfulCheckAtUtc.Value.ToUniversalTime()
                >= AutomaticCheckInterval);

    public UpdatePreferences WithSuccessfulCheck(
        DateTimeOffset checkedAtUtc,
        string observedVersion) =>
        this with
        {
            LastSuccessfulCheckAtUtc = checkedAtUtc.ToUniversalTime(),
            LastObservedVersion = observedVersion,
            LastError = null,
            UpdatedAtUtc = checkedAtUtc.ToUniversalTime(),
        };

    public UpdatePreferences WithError(
        DateTimeOffset attemptedAtUtc,
        string error) =>
        this with
        {
            LastError = string.IsNullOrWhiteSpace(error)
                ? "Nieznany błąd aktualizacji."
                : error[..Math.Min(error.Length, 1000)],
            UpdatedAtUtc = attemptedAtUtc.ToUniversalTime(),
        };
}

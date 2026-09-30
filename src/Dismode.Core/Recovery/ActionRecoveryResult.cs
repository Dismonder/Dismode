namespace Dismode.Core.Recovery;

public sealed record ActionRecoveryResult(
    ActionRecoveryStatus Status,
    string? Details);


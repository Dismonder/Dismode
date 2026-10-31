namespace Dismode.Core.Actions;

public sealed record PreparedAction<TState>(
    TState OriginalState,
    TState DesiredState,
    DateTimeOffset PreparedAtUtc)
    where TState : notnull
{
    public bool IsUnchanged { get; init; }

    public DateTimeOffset PreparedAtUtc { get; init; } = PreparedAtUtc.ToUniversalTime();
}


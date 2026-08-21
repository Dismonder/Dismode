namespace GameShift.Core.Transactions;

public sealed record ActionExecutionResult(
    ActionExecutionStatus Status,
    string? Details);


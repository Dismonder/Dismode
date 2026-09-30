namespace Dismode.Core.Policies;

public sealed record SafetyDecision(
    bool IsAllowed,
    SafetyReasonCode ReasonCode,
    string Explanation);


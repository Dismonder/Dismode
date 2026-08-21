namespace GameShift.Core.Policies;

public sealed record SafetyDecision(
    bool IsAllowed,
    SafetyReasonCode ReasonCode,
    string Explanation);


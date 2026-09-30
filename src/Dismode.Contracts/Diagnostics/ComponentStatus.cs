namespace Dismode.Contracts.Diagnostics;

public sealed record ComponentStatus(
    string Component,
    string State,
    int ProtocolVersion,
    DateTimeOffset ObservedAtUtc);


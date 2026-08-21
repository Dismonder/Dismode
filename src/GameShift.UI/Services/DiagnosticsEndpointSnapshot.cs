namespace GameShift.UI.Services;

public sealed record DiagnosticsEndpointSnapshot(
    string Component,
    bool IsAvailable,
    bool IsCompatible,
    string State,
    string Message,
    int ProtocolVersion,
    int ProcessCount,
    int ServiceCount,
    bool IsTruncated,
    long LatencyMilliseconds);

namespace GameShift.Contracts.Protocol;

public static class ProtocolInfo
{
    public const int CurrentVersion = 4;
    public const int MaximumMessageBytes = 1024 * 1024;
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(2);
}

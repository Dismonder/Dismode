namespace GameShift.Core.Activation;

public static class CompanionStartupPolicy
{
    private static readonly HashSet<string> InteractiveBackgroundExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GameShift.SessionHost.exe",
        };

    public static IReadOnlyCollection<string> InteractiveBackgroundComponents =>
        InteractiveBackgroundExecutables;

    public static bool ShouldStartForInteractiveUser(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        return InteractiveBackgroundExecutables.Contains(executableName);
    }
}

namespace Dismode.Windows.Profiles;

public sealed record DetectedGame(
    string Source,
    string ExternalId,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> LaunchArguments,
    int Confidence,
    string? CatalogNamespace = null);

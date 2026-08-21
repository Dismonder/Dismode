namespace GameShift.Core.Profiles;

public sealed record GameMetadataProviderResult(
    string? LauncherPath,
    DateTimeOffset? LastPlayedAtUtc,
    long? TotalPlaytimeMinutes,
    string? HeroArtworkPath);

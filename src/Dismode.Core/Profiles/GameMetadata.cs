using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Profiles;

public sealed record GameMetadata
{
    private const int MaximumSourceLength = 64;
    private const int MaximumExternalIdLength = 256;

    public GameMetadata(
        GameProfileId profileId,
        string source,
        string? externalId,
        string? launcherPath,
        DateTimeOffset? lastPlayedAtUtc,
        long totalPlaytimeMinutes,
        string? heroArtworkPath,
        DateTimeOffset lastMetadataRefreshAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        string normalizedSource = source.Trim();
        if (normalizedSource.Length > MaximumSourceLength
            || normalizedSource.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The game metadata source is invalid.",
                nameof(source));
        }

        string? normalizedExternalId = NormalizeOptionalText(
            externalId,
            MaximumExternalIdLength,
            nameof(externalId));
        if (totalPlaytimeMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalPlaytimeMinutes),
                totalPlaytimeMinutes,
                "Total playtime cannot be negative.");
        }

        ProfileId = profileId;
        Source = normalizedSource;
        ExternalId = normalizedExternalId;
        LauncherPath = NormalizeLocalPath(
            launcherPath,
            requireImageExtension: false,
            nameof(launcherPath));
        LastPlayedAtUtc = lastPlayedAtUtc?.ToUniversalTime();
        TotalPlaytimeMinutes = totalPlaytimeMinutes;
        HeroArtworkPath = NormalizeLocalPath(
            heroArtworkPath,
            requireImageExtension: true,
            nameof(heroArtworkPath));
        LastMetadataRefreshAtUtc =
            lastMetadataRefreshAtUtc.ToUniversalTime();
    }

    public GameProfileId ProfileId { get; }

    public string Source { get; }

    public string? ExternalId { get; }

    public string? LauncherPath { get; }

    public DateTimeOffset? LastPlayedAtUtc { get; }

    public long TotalPlaytimeMinutes { get; }

    public string? HeroArtworkPath { get; }

    public DateTimeOffset LastMetadataRefreshAtUtc { get; }

    private static string? NormalizeOptionalText(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The optional game metadata value is invalid.",
                parameterName);
        }

        return normalized;
    }

    private static string? NormalizeLocalPath(
        string? value,
        bool requireImageExtension,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                && (!uri.IsFile || uri.IsUnc)
            || requireImageExtension
                && !SupportedArtworkExtensions.Contains(
                    Path.GetExtension(value)))
        {
            throw new ArgumentException(
                "Metadata paths must be fully qualified local paths.",
                parameterName);
        }

        return Path.GetFullPath(value);
    }

    private static readonly HashSet<string> SupportedArtworkExtensions = new(
        [".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);
}

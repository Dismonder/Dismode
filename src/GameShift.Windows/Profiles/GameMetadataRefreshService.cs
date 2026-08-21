using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;

namespace GameShift.Windows.Profiles;

public sealed class GameMetadataRefreshService
{
    public static readonly TimeSpan DefaultTimeToLive =
        TimeSpan.FromHours(6);

    private readonly Dictionary<string, IGameMetadataProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;

    public GameMetadataRefreshService(
        IEnumerable<IGameMetadataProvider>? providers = null,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null)
    {
        _providers = (providers
                ??
                [
                    new SteamLocalGameMetadataProvider(),
                    new EpicLocalGameMetadataProvider(),
                ])
            .GroupBy(provider => provider.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeToLive = timeToLive ?? DefaultTimeToLive;
        if (_timeToLive < TimeSpan.FromMinutes(5)
            || _timeToLive > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeToLive),
                _timeToLive,
                "The metadata TTL must be between five minutes and seven days.");
        }
    }

    public async ValueTask<GameMetadataRefreshResult> RefreshDetectedAsync(
        IGameProfileRepository profileRepository,
        IGameMetadataRepository metadataRepository,
        IReadOnlyList<DetectedGame> detectedGames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileRepository);
        ArgumentNullException.ThrowIfNull(metadataRepository);
        ArgumentNullException.ThrowIfNull(detectedGames);
        await profileRepository.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        await metadataRepository.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ManualGameProfile> profiles =
            await profileRepository.ListAsync(cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyList<GameMetadata> existingMetadata =
            await metadataRepository.ListMetadataAsync(cancellationToken)
                .ConfigureAwait(false);
        Dictionary<string, ManualGameProfile> profilesByPath =
            profiles.ToDictionary(
                profile => profile.ExecutablePath,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<GameProfileId, GameMetadata> metadataByProfile =
            existingMetadata.ToDictionary(metadata => metadata.ProfileId);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int refreshedCount = 0;
        int skippedFreshCount = 0;
        int errorCount = 0;

        foreach (DetectedGame detected in detectedGames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string executablePath;
            try
            {
                executablePath = Path.GetFullPath(detected.ExecutablePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException)
            {
                errorCount++;
                continue;
            }

            if (!profilesByPath.TryGetValue(
                    executablePath,
                    out ManualGameProfile? profile))
            {
                continue;
            }

            metadataByProfile.TryGetValue(
                profile.ProfileId,
                out GameMetadata? existing);
            bool identityUnchanged = existing is not null
                && existing.Source.Equals(
                    detected.Source,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    existing.ExternalId,
                    NormalizeExternalId(detected.ExternalId),
                    StringComparison.OrdinalIgnoreCase);
            if (identityUnchanged
                && IsFresh(existing!.LastMetadataRefreshAtUtc, now)
                && !NeedsInitialProviderEnrichment(profile, existing))
            {
                skippedFreshCount++;
                continue;
            }

            GameMetadataProviderResult? providerResult;
            try
            {
                providerResult = await ReadProviderAsync(
                        profile,
                        detected.Source,
                        detected.ExternalId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
                    or ArgumentException
                    or NotSupportedException)
            {
                errorCount++;
                continue;
            }

            GameMetadata refreshed = CreateMetadata(
                profile,
                detected.Source,
                detected.ExternalId,
                existing,
                providerResult,
                now,
                observedSessionMinutes: null,
                observedLastPlayedAtUtc: null);
            await metadataRepository.UpsertMetadataAsync(
                    refreshed,
                    cancellationToken)
                .ConfigureAwait(false);
            metadataByProfile[profile.ProfileId] = refreshed;
            refreshedCount++;
        }

        return new(
            refreshedCount,
            skippedFreshCount,
            errorCount);
    }

    public async ValueTask<bool> RefreshAfterSessionAsync(
        IGameProfileRepository profileRepository,
        IGameMetadataRepository metadataRepository,
        GameProfileId profileId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileRepository);
        ArgumentNullException.ThrowIfNull(metadataRepository);
        DateTimeOffset started = startedAtUtc.ToUniversalTime();
        DateTimeOffset ended = endedAtUtc.ToUniversalTime();
        if (ended < started)
        {
            throw new ArgumentException(
                "A metadata session cannot end before it starts.",
                nameof(endedAtUtc));
        }

        await profileRepository.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        await metadataRepository.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        ManualGameProfile? profile = await profileRepository.FindAsync(
                profileId,
                cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return false;
        }

        GameMetadata? existing =
            await metadataRepository.FindMetadataAsync(
                    profileId,
                    cancellationToken)
                .ConfigureAwait(false);
        string source = existing?.Source ?? "Manual";
        string? externalId = existing?.ExternalId;
        GameMetadataProviderResult? providerResult;
        try
        {
            providerResult = await ReadProviderAsync(
                    profile,
                    source,
                    externalId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is
                IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException
                or NotSupportedException)
        {
            providerResult = null;
        }

        long observedMinutes = Math.Max(
            1,
            (long)Math.Ceiling((ended - started).TotalMinutes));
        GameMetadata refreshed = CreateMetadata(
            profile,
            source,
            externalId,
            existing,
            providerResult,
            _timeProvider.GetUtcNow(),
            observedMinutes,
            ended);
        await metadataRepository.UpsertMetadataAsync(
                refreshed,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async ValueTask<GameMetadataProviderResult?> ReadProviderAsync(
        ManualGameProfile profile,
        string source,
        string? externalId,
        CancellationToken cancellationToken)
    {
        return _providers.TryGetValue(
            source,
            out IGameMetadataProvider? provider)
            ? await provider.ReadAsync(
                    profile,
                    externalId,
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
    }

    private static GameMetadata CreateMetadata(
        ManualGameProfile profile,
        string source,
        string? externalId,
        GameMetadata? existing,
        GameMetadataProviderResult? providerResult,
        DateTimeOffset refreshedAtUtc,
        long? observedSessionMinutes,
        DateTimeOffset? observedLastPlayedAtUtc)
    {
        long totalPlaytime = providerResult?.TotalPlaytimeMinutes
            ?? existing?.TotalPlaytimeMinutes
            ?? 0;
        if (observedSessionMinutes is long observed)
        {
            long priorWithObservedSession = SaturatingAdd(
                existing?.TotalPlaytimeMinutes ?? 0,
                observed);
            totalPlaytime = Math.Max(
                totalPlaytime,
                priorWithObservedSession);
        }

        DateTimeOffset? lastPlayedAtUtc = MaxTimestamp(
            existing?.LastPlayedAtUtc,
            providerResult?.LastPlayedAtUtc);
        lastPlayedAtUtc = MaxTimestamp(
            lastPlayedAtUtc,
            observedLastPlayedAtUtc);
        return new(
            profile.ProfileId,
            source,
            NormalizeExternalId(externalId),
            providerResult?.LauncherPath ?? existing?.LauncherPath,
            lastPlayedAtUtc,
            totalPlaytime,
            providerResult?.HeroArtworkPath
                ?? profile.ArtworkPath
                ?? existing?.HeroArtworkPath,
            refreshedAtUtc);
    }

    private bool IsFresh(
        DateTimeOffset lastRefreshAtUtc,
        DateTimeOffset now) =>
        lastRefreshAtUtc >= now
        || now - lastRefreshAtUtc < _timeToLive;

    private bool NeedsInitialProviderEnrichment(
        ManualGameProfile profile,
        GameMetadata metadata)
    {
        if (!_providers.ContainsKey(metadata.Source))
        {
            return false;
        }

        bool hasProviderArtwork = metadata.HeroArtworkPath is not null
            && !string.Equals(
                metadata.HeroArtworkPath,
                profile.ArtworkPath,
                StringComparison.OrdinalIgnoreCase);
        return metadata.LauncherPath is null
            && metadata.LastPlayedAtUtc is null
            && metadata.TotalPlaytimeMinutes == 0
            && !hasProviderArtwork;
    }

    private static string? NormalizeExternalId(string? externalId) =>
        string.IsNullOrWhiteSpace(externalId)
            ? null
            : externalId.Trim();

    private static DateTimeOffset? MaxTimestamp(
        DateTimeOffset? first,
        DateTimeOffset? second) =>
        first is null
            ? second
            : second is null || first >= second
                ? first
                : second;

    private static long SaturatingAdd(long first, long second) =>
        first > long.MaxValue - second
            ? long.MaxValue
            : first + second;
}

public sealed record GameMetadataRefreshResult(
    int RefreshedCount,
    int SkippedFreshCount,
    int ErrorCount);

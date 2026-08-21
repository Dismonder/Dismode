using GameShift.Core.Profiles;

namespace GameShift.Windows.Profiles;

public sealed class GameLibrarySyncService
{
    private readonly ManualGameProfileFactory _profileFactory;
    private readonly LocalGameArtworkResolver _artworkResolver;
    private readonly TimeProvider _timeProvider;
    private readonly GameMetadataRefreshService _metadataRefreshService;
    private readonly Func<CancellationToken, Task<DetectedGame[]>>
        _discoverGamesAsync;

    public GameLibrarySyncService(
        ManualGameProfileFactory? profileFactory = null,
        TimeProvider? timeProvider = null,
        LocalGameArtworkResolver? artworkResolver = null,
        GameMetadataRefreshService? metadataRefreshService = null)
        : this(
            profileFactory,
            timeProvider,
            artworkResolver,
            InstalledGameDiscoveryService.DiscoverAsync,
            metadataRefreshService)
    {
    }

    internal GameLibrarySyncService(
        ManualGameProfileFactory? profileFactory,
        TimeProvider? timeProvider,
        LocalGameArtworkResolver? artworkResolver,
        Func<CancellationToken, Task<DetectedGame[]>> discoverGamesAsync,
        GameMetadataRefreshService? metadataRefreshService = null)
    {
        ArgumentNullException.ThrowIfNull(discoverGamesAsync);
        _artworkResolver = artworkResolver ?? new();
        _profileFactory = profileFactory
            ?? new(artworkResolver: _artworkResolver);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _discoverGamesAsync = discoverGamesAsync;
        _metadataRefreshService = metadataRefreshService ?? new();
    }

    public async ValueTask<GameLibrarySyncResult> SyncAsync(
        IGameProfileRepository repository,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        await repository.InitializeAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ManualGameProfile> existingProfiles =
            await repository.ListAsync(cancellationToken)
                .ConfigureAwait(false);
        Dictionary<string, ManualGameProfile> existingByPath =
            existingProfiles.ToDictionary(
                profile => profile.ExecutablePath,
                StringComparer.OrdinalIgnoreCase);
        DetectedGame[] detected = await _discoverGamesAsync(
                cancellationToken)
            .ConfigureAwait(false);

        int addedCount = 0;
        int updatedCount = 0;
        int errorCount = 0;
        foreach (DetectedGame game in detected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string detectedPath = Path.GetFullPath(
                    game.ExecutablePath);
                existingByPath.TryGetValue(
                    detectedPath,
                    out ManualGameProfile? existing);
                if (existing is null)
                {
                    ManualGameProfile[] relocationCandidates =
                        existingProfiles
                            .Where(profile =>
                                (!File.Exists(profile.ExecutablePath)
                                    || IsKnownVersionedRelocation(
                                        game,
                                        profile.ExecutablePath,
                                        detectedPath))
                                && string.Equals(
                                    Path.GetFileName(
                                        profile.ExecutablePath),
                                    Path.GetFileName(detectedPath),
                                    StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                    if (relocationCandidates.Length == 1)
                    {
                        existing = relocationCandidates[0];
                    }
                }

                if (game.Source.Equals(
                        "Roblox",
                        StringComparison.OrdinalIgnoreCase))
                {
                    ManualGameProfile[] obsoleteVersions =
                        existingByPath.Values
                            .Where(profile =>
                                profile.ProfileId
                                    != existing?.ProfileId
                                && IsKnownVersionedRelocation(
                                    game,
                                    profile.ExecutablePath,
                                    detectedPath))
                            .ToArray();
                    foreach (ManualGameProfile obsolete
                                 in obsoleteVersions)
                    {
                        if (await repository.DeleteAsync(
                                obsolete.ProfileId,
                                cancellationToken)
                            .ConfigureAwait(false))
                        {
                            existingByPath.Remove(
                                obsolete.ExecutablePath);
                            updatedCount++;
                        }
                    }
                }

                bool executableRelocated =
                    existing is not null
                    && !StringComparer.OrdinalIgnoreCase.Equals(
                        existing.ExecutablePath,
                        detectedPath);
                if (existing is not null
                    && !executableRelocated
                    && File.GetLastWriteTimeUtc(detectedPath)
                        <= existing.UpdatedAtUtc.UtcDateTime)
                {
                    GameArtwork? artwork =
                        await _artworkResolver.ResolveAsync(
                                game,
                                cancellationToken)
                            .ConfigureAwait(false);
                    string? resolvedArtworkPath =
                        artwork?.LocalPath ?? existing.ArtworkPath;
                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                            existing.ArtworkPath,
                            resolvedArtworkPath))
                    {
                        ManualGameProfile artworkRefreshed = new(
                            existing.ProfileId,
                            existing.DisplayName,
                            existing.ExecutablePath,
                            existing.ExecutableSha256,
                            existing.WorkingDirectory,
                            existing.LaunchArguments,
                            existing.Preset,
                            existing.IsEnabled,
                            existing.CreatedAtUtc,
                            _timeProvider.GetUtcNow(),
                            resolvedArtworkPath);
                        await repository.UpsertAsync(
                                artworkRefreshed,
                                cancellationToken)
                            .ConfigureAwait(false);
                        existingByPath[artworkRefreshed.ExecutablePath] =
                            artworkRefreshed;
                        updatedCount++;
                    }

                    continue;
                }

                ManualGameProfile scanned =
                    await _profileFactory.CreateDetectedAsync(
                            game,
                            OptimizationPreset.Balanced,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (existing is null)
                {
                    await repository.UpsertAsync(
                            scanned,
                            cancellationToken)
                        .ConfigureAwait(false);
                    existingByPath.Add(
                        scanned.ExecutablePath,
                        scanned);
                    addedCount++;
                    continue;
                }

                string? refreshedArtworkPath =
                    scanned.ArtworkPath ?? existing.ArtworkPath;
                bool executableHashUnchanged =
                    StringComparer.OrdinalIgnoreCase.Equals(
                        existing.ExecutableSha256,
                        scanned.ExecutableSha256);
                bool artworkPathUnchanged =
                    StringComparer.OrdinalIgnoreCase.Equals(
                        existing.ArtworkPath,
                        refreshedArtworkPath);
                if (executableHashUnchanged
                    && !executableRelocated
                    && artworkPathUnchanged)
                {
                    continue;
                }

                ManualGameProfile refreshed = new(
                    existing.ProfileId,
                    existing.DisplayName,
                    detectedPath,
                    scanned.ExecutableSha256,
                    Path.GetDirectoryName(detectedPath)
                        ?? scanned.WorkingDirectory,
                    existing.LaunchArguments,
                    existing.Preset,
                    existing.IsEnabled,
                    existing.CreatedAtUtc,
                    _timeProvider.GetUtcNow(),
                    refreshedArtworkPath);
                await repository.UpsertAsync(
                        refreshed,
                        cancellationToken)
                    .ConfigureAwait(false);
                existingByPath.Remove(existing.ExecutablePath);
                existingByPath[refreshed.ExecutablePath] =
                    refreshed;
                updatedCount++;
            }
            catch (Exception exception) when (
                exception is
                    IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException
                    or ArgumentException)
            {
                errorCount++;
            }
        }

        if (repository is IGameMetadataRepository metadataRepository)
        {
            GameMetadataRefreshResult metadataResult =
                await _metadataRefreshService.RefreshDetectedAsync(
                        repository,
                        metadataRepository,
                        detected,
                        cancellationToken)
                    .ConfigureAwait(false);
            errorCount += metadataResult.ErrorCount;
        }

        return new(
            detected.Length,
            addedCount,
            updatedCount,
            errorCount);
    }

    private static bool IsKnownVersionedRelocation(
        DetectedGame game,
        string existingPath,
        string detectedPath)
    {
        if (!game.Source.Equals(
                "Roblox",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(existingPath),
                Path.GetFileName(detectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string versionDirectoryMarker =
            $"{Path.DirectorySeparatorChar}Roblox"
            + $"{Path.DirectorySeparatorChar}Versions"
            + $"{Path.DirectorySeparatorChar}";
        return existingPath.Contains(
                versionDirectoryMarker,
                StringComparison.OrdinalIgnoreCase)
            && detectedPath.Contains(
                versionDirectoryMarker,
                StringComparison.OrdinalIgnoreCase)
            && !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(existingPath),
                Path.GetFullPath(detectedPath));
    }
}

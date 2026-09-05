using System.Net;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class GameLibrarySyncServiceTests
{
    [TestMethod]
    public async Task FreshUnchangedMetadataIsNotUpsertedAfterProviderTtlSkip()
    {
        using UserDataTestContext context = new();
        DateTimeOffset now = new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string steamRoot = Path.Combine(context.DirectoryPath, "Steam");
        string posterPath = CreateFile(
            steamRoot,
            Path.Combine(
                "appcache",
                "librarycache",
                "12345",
                "library_600x900.jpg"));
        string heroPath = CreateFile(
            steamRoot,
            Path.Combine(
                "appcache",
                "librarycache",
                "12345",
                "library_hero.jpg"));
        string launcherPath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Steam", "steam.exe"));
        File.SetLastWriteTimeUtc(executablePath, now.AddHours(-3).UtcDateTime);
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Game",
            executablePath,
            await ExecutableFileHasher.ComputeSha256Async(
                executablePath,
                CancellationToken.None),
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now.AddDays(-2),
            now.AddHours(-2),
            posterPath);
        GameMetadata metadata = new(
            profile.ProfileId,
            "Steam",
            "12345",
            launcherPath,
            now.AddHours(-2),
            totalPlaytimeMinutes: 321,
            heroPath,
            now.AddHours(-1));
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(metadata, CancellationToken.None);

        CountingGameRepository repository = new(
            context.Store,
            context.Store);
        CountingMetadataProvider provider = new();
        FixedTimeProvider timeProvider = new(now);
        LocalGameArtworkResolver resolver = new(
            [steamRoot],
            Path.Combine(context.DirectoryPath, "ArtworkCache"));
        DetectedGame detected = new(
            "Steam",
            "12345",
            "Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);
        GameLibrarySyncService service = new(
            new(timeProvider, resolver),
            timeProvider,
            resolver,
            _ => Task.FromResult(new[] { detected }),
            new([provider], timeProvider, TimeSpan.FromHours(6)));

        GameLibrarySyncResult result = await service.SyncAsync(
            repository,
            CancellationToken.None);
        GameMetadata? stored = await repository.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);

        Assert.AreEqual(0, result.ErrorCount);
        Assert.AreEqual(0, provider.InvocationCount);
        Assert.AreEqual(0, repository.MetadataUpsertCount);
        Assert.AreEqual(metadata, stored);
    }

    [TestMethod]
    public async Task StaleMetadataRefreshPreservesValidHeroWithoutArtworkTransportOrExtraction()
    {
        using UserDataTestContext context = new();
        DateTimeOffset now = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);
        DateTimeOffset usageAt = now.AddMinutes(-30);
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string posterPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "poster.bmp"),
            width: 600,
            height: 900);
        string heroPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "hero.bmp"),
            width: 1920,
            height: 620);
        string steamRoot = Path.Combine(context.DirectoryPath, "Steam");
        string localConfigPath = Path.Combine(
            steamRoot,
            "userdata",
            "100",
            "config",
            "localconfig.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(localConfigPath)!);
        await File.WriteAllTextAsync(
            localConfigPath,
            $$"""
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "12345"
                                {
                                    "LastPlayed" "{{usageAt.ToUnixTimeSeconds()}}"
                                    "Playtime" "95"
                                }
                            }
                        }
                    }
                }
            }
            """);
        File.SetLastWriteTimeUtc(executablePath, now.AddHours(-3).UtcDateTime);
        ManualGameProfile profile = await CreateStoredProfileAsync(
            executablePath,
            posterPath,
            now);
        GameMetadata metadata = new(
            profile.ProfileId,
            "Steam",
            "12345",
            launcherPath: null,
            lastPlayedAtUtc: now.AddDays(-1),
            totalPlaytimeMinutes: 10,
            heroPath,
            now.AddHours(-7));
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(metadata, CancellationToken.None);

        CountingGameRepository repository = new(context.Store, context.Store);
        CountingNullArtworkExtractor extractor = new();
        using CountingNotFoundHandler handler = new();
        FixedTimeProvider timeProvider = new(now);
        LocalGameArtworkResolver resolver = new(
            [steamRoot],
            Path.Combine(context.DirectoryPath, "Cache"),
            extractor,
            handler);
        SteamLocalGameMetadataProvider provider = new([steamRoot]);
        DetectedGame detected = CreateSteamDetectedGame(executablePath);
        GameLibrarySyncService service = new(
            new(timeProvider, resolver),
            timeProvider,
            resolver,
            _ => Task.FromResult(new[] { detected }),
            new([provider], timeProvider, TimeSpan.FromHours(6)));

        GameLibrarySyncResult result = await service.SyncAsync(
            repository,
            CancellationToken.None);
        GameMetadata? stored = await repository.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);

        Assert.AreEqual(0, result.ErrorCount);
        Assert.IsNotNull(stored);
        Assert.AreEqual(usageAt, stored.LastPlayedAtUtc);
        Assert.AreEqual(95L, stored.TotalPlaytimeMinutes);
        Assert.AreEqual(now, stored.LastMetadataRefreshAtUtc);
        Assert.AreEqual(heroPath, stored.HeroArtworkPath);
        Assert.AreEqual(1, repository.MetadataUpsertCount);
        Assert.AreEqual(0, handler.InvocationCount);
        Assert.AreEqual(0, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task ValidStoredPosterAndHeroSkipTransportAndExtractor()
    {
        using UserDataTestContext context = new();
        DateTimeOffset now = new(2026, 8, 23, 11, 0, 0, TimeSpan.Zero);
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string posterPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "poster.bmp"),
            width: 600,
            height: 900);
        string heroPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "hero.bmp"),
            width: 1920,
            height: 620);
        File.SetLastWriteTimeUtc(executablePath, now.AddHours(-1).UtcDateTime);
        ManualGameProfile profile = await CreateStoredProfileAsync(
            executablePath,
            posterPath,
            now);
        GameMetadata metadata = CreateStoredMetadata(
            profile.ProfileId,
            heroPath,
            now);
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(metadata, CancellationToken.None);

        CountingGameRepository repository = new(context.Store, context.Store);
        CountingNullArtworkExtractor extractor = new();
        using CountingNotFoundHandler handler = new();
        FixedTimeProvider timeProvider = new(now);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: Path.Combine(context.DirectoryPath, "Cache"),
            executableExtractor: extractor,
            artworkHttpHandler: handler);
        DetectedGame detected = CreateSteamDetectedGame(executablePath);
        GameLibrarySyncService service = CreateSyncService(
            resolver,
            timeProvider,
            detected);

        _ = await service.SyncAsync(repository, CancellationToken.None);

        Assert.AreEqual(0, handler.InvocationCount);
        Assert.AreEqual(0, extractor.InvocationCount);
        Assert.AreEqual(0, repository.MetadataUpsertCount);
        Assert.AreEqual(
            posterPath,
            (await repository.FindAsync(profile.ProfileId, CancellationToken.None))
                ?.ArtworkPath);
        Assert.AreEqual(
            metadata,
            await repository.FindMetadataAsync(
                profile.ProfileId,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task WideHeroStoredAsPosterIsClearedWithoutRefreshingValidHero()
    {
        using UserDataTestContext context = new();
        DateTimeOffset now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string wrongPosterPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "wide-as-poster.bmp"),
            width: 1920,
            height: 620);
        string heroPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "hero.bmp"),
            width: 1920,
            height: 620);
        File.SetLastWriteTimeUtc(executablePath, now.AddHours(-3).UtcDateTime);
        ManualGameProfile profile = await CreateStoredProfileAsync(
            executablePath,
            wrongPosterPath,
            now);
        GameMetadata metadata = CreateStoredMetadata(
            profile.ProfileId,
            heroPath,
            now);
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(metadata, CancellationToken.None);

        CountingGameRepository repository = new(context.Store, context.Store);
        CountingNullArtworkExtractor extractor = new();
        using CountingNotFoundHandler handler = new();
        FixedTimeProvider timeProvider = new(now);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: Path.Combine(context.DirectoryPath, "Cache"),
            executableExtractor: extractor,
            artworkHttpHandler: handler);
        DetectedGame detected = CreateSteamDetectedGame(executablePath);
        GameLibrarySyncService service = CreateSyncService(
            resolver,
            timeProvider,
            detected);

        _ = await service.SyncAsync(repository, CancellationToken.None);
        ManualGameProfile? storedProfile = await repository.FindAsync(
            profile.ProfileId,
            CancellationToken.None);
        GameMetadata? storedMetadata = await repository.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);

        Assert.IsNotNull(storedProfile);
        Assert.IsNull(storedProfile.ArtworkPath);
        Assert.AreEqual(metadata, storedMetadata);
        Assert.AreEqual(0, repository.MetadataUpsertCount);
        Assert.AreEqual(2, handler.InvocationCount);
        Assert.IsTrue(handler.RequestUris.All(uri =>
            uri.AbsolutePath.Contains(
                "library_600x900",
                StringComparison.Ordinal)));
        Assert.AreEqual(1, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task CorruptStoredHeroIsReplacedWithoutRefreshingValidPoster()
    {
        using UserDataTestContext context = new();
        DateTimeOffset now = new(2026, 8, 23, 13, 0, 0, TimeSpan.Zero);
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string posterPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine("Stored", "poster.bmp"),
            width: 600,
            height: 900);
        string corruptHeroPath = Path.Combine(
            context.DirectoryPath,
            "Stored",
            "corrupt-hero.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(corruptHeroPath)!);
        File.WriteAllBytes(corruptHeroPath, [0x42, 0x4D, 0x00]);
        string steamRoot = Path.Combine(context.DirectoryPath, "Steam");
        string replacementHeroPath = CreateFile(
            steamRoot,
            Path.Combine(
                "appcache",
                "librarycache",
                "12345",
                "library_hero.jpg"));
        File.SetLastWriteTimeUtc(executablePath, now.AddHours(-3).UtcDateTime);
        ManualGameProfile profile = await CreateStoredProfileAsync(
            executablePath,
            posterPath,
            now);
        GameMetadata metadata = CreateStoredMetadata(
            profile.ProfileId,
            corruptHeroPath,
            now);
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(metadata, CancellationToken.None);

        CountingGameRepository repository = new(context.Store, context.Store);
        CountingNullArtworkExtractor extractor = new();
        using CountingNotFoundHandler handler = new();
        FixedTimeProvider timeProvider = new(now);
        LocalGameArtworkResolver resolver = new(
            [steamRoot],
            Path.Combine(context.DirectoryPath, "Cache"),
            extractor,
            handler);
        DetectedGame detected = CreateSteamDetectedGame(executablePath);
        GameLibrarySyncService service = CreateSyncService(
            resolver,
            timeProvider,
            detected);

        _ = await service.SyncAsync(repository, CancellationToken.None);
        ManualGameProfile? storedProfile = await repository.FindAsync(
            profile.ProfileId,
            CancellationToken.None);
        GameMetadata? storedMetadata = await repository.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);

        Assert.IsNotNull(storedProfile);
        Assert.IsNotNull(storedMetadata);
        Assert.AreEqual(posterPath, storedProfile.ArtworkPath);
        Assert.AreEqual(replacementHeroPath, storedMetadata.HeroArtworkPath);
        Assert.AreEqual(metadata.Source, storedMetadata.Source);
        Assert.AreEqual(metadata.ExternalId, storedMetadata.ExternalId);
        Assert.AreEqual(metadata.LastPlayedAtUtc, storedMetadata.LastPlayedAtUtc);
        Assert.AreEqual(
            metadata.TotalPlaytimeMinutes,
            storedMetadata.TotalPlaytimeMinutes);
        Assert.AreEqual(
            metadata.LastMetadataRefreshAtUtc,
            storedMetadata.LastMetadataRefreshAtUtc);
        Assert.AreEqual(1, repository.MetadataUpsertCount);
        Assert.AreEqual(0, handler.InvocationCount);
        Assert.AreEqual(0, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task UnchangedExecutableHashPersistsMissingArtworkPath()
    {
        ArtworkRefreshOutcome outcome =
            await RunArtworkRefreshAsync(
                useStoredPoster: false,
                executableIsNewer: true);

        AssertRefreshPersisted(outcome);
        Assert.IsNull(outcome.StoredArtworkPath);
    }

    [TestMethod]
    public async Task UnchangedExecutableHashPreservesValidArtworkPath()
    {
        ArtworkRefreshOutcome outcome =
            await RunArtworkRefreshAsync(
                useStoredPoster: true,
                executableIsNewer: false);

        Assert.AreEqual(1, outcome.SyncResult.DetectedCount);
        Assert.AreEqual(0, outcome.SyncResult.AddedCount);
        Assert.AreEqual(0, outcome.SyncResult.UpdatedCount);
        Assert.AreEqual(0, outcome.SyncResult.ErrorCount);
        Assert.AreEqual(
            outcome.ExistingProfile.ProfileId,
            outcome.UpdatedProfile.ProfileId);
        Assert.AreEqual(
            outcome.ExistingProfile.ExecutableSha256,
            outcome.UpdatedProfile.ExecutableSha256);
        Assert.IsNotNull(outcome.StoredArtworkPath);
        Assert.AreEqual(
            outcome.StoredArtworkPath,
            outcome.UpdatedProfile.ArtworkPath);
        Assert.AreEqual("Steam", outcome.Metadata.Source);
        Assert.AreEqual("12345", outcome.Metadata.ExternalId);
        Assert.AreEqual(outcome.HeroPath, outcome.Metadata.HeroArtworkPath);
    }

    [TestMethod]
    public async Task ExistingRobloxTextureArtworkIsReplacedWithExeThumbnail()
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Roblox", "RobloxPlayerBeta.exe"));
        string invalidArtworkPath = CreateArtworkFile(
            context.DirectoryPath,
            Path.Combine(
                "Roblox",
                "content",
                "textures",
                "translateIcon.png"),
            width: 256,
            height: 256);
        string thumbnailPath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Artwork", "Roblox.bmp"));
        DateTimeOffset createdAt = new(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddDays(2);
        File.SetLastWriteTimeUtc(
            executablePath,
            createdAt.AddDays(1).UtcDateTime);
        string executableHash =
            await ExecutableFileHasher.ComputeSha256Async(
                executablePath,
                CancellationToken.None);
        ManualGameProfile existingProfile = new(
            GameProfileId.Create(),
            "Roblox",
            executablePath,
            executableHash,
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            createdAt,
            updatedAt,
            invalidArtworkPath);
        await context.Store.UpsertAsync(
            existingProfile,
            CancellationToken.None);

        DetectedGame detectedGame = new(
            "Roblox",
            "RobloxPlayer",
            "Roblox",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);
        LocalGameArtworkResolver artworkResolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: Path.Combine(
                context.DirectoryPath,
                "ArtworkCache"),
            executableExtractor:
                new FixedExecutableArtworkExtractor(thumbnailPath));
        GameLibrarySyncService service = new(
            new(artworkResolver: artworkResolver),
            TimeProvider.System,
            artworkResolver,
            _ => Task.FromResult(new[] { detectedGame }),
            new(providers: []));

        GameLibrarySyncResult syncResult = await service.SyncAsync(
            context.Store,
            CancellationToken.None);
        ManualGameProfile? updatedProfile =
            await context.Store.FindAsync(
                existingProfile.ProfileId,
                CancellationToken.None);

        Assert.IsNotNull(updatedProfile);
        Assert.AreEqual(1, syncResult.UpdatedCount);
        Assert.AreEqual(0, syncResult.ErrorCount);
        Assert.AreEqual(thumbnailPath, updatedProfile.ArtworkPath);
        Assert.AreNotEqual(
            invalidArtworkPath,
            updatedProfile.ArtworkPath);
    }

    private static async Task<ArtworkRefreshOutcome>
        RunArtworkRefreshAsync(
            bool useStoredPoster,
            bool executableIsNewer)
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Game", "Game.exe"));
        string steamRoot = Path.Combine(
            context.DirectoryPath,
            "Steam");
        string heroPath = CreateFile(
            steamRoot,
            Path.Combine(
                "appcache",
                "librarycache",
                "12345",
                "library_hero.jpg"));
        string posterPath = CreateFile(
            steamRoot,
            Path.Combine(
                "appcache",
                "librarycache",
                "12345",
                "library_600x900.jpg"));
        string? storedArtworkPath = useStoredPoster
            ? CreateFile(
                steamRoot,
                Path.Combine(
                    "appcache",
                    "librarycache",
                    "12345",
                    "legacy_600x900.jpg"))
            : null;

        DateTimeOffset createdAt = new(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset updatedAt = createdAt.AddDays(1);
        File.SetLastWriteTimeUtc(
            executablePath,
            updatedAt.AddDays(executableIsNewer ? 1 : -1).UtcDateTime);
        string executableHash =
            await ExecutableFileHasher.ComputeSha256Async(
                executablePath,
                CancellationToken.None);
        ManualGameProfile existingProfile = new(
            GameProfileId.Create(),
            "Game",
            executablePath,
            executableHash,
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            createdAt,
            updatedAt,
            storedArtworkPath);
        await context.Store.UpsertAsync(
            existingProfile,
            CancellationToken.None);

        DetectedGame detectedGame = new(
            "Steam",
            "12345",
            "Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);
        LocalGameArtworkResolver artworkResolver = new(
            [steamRoot],
            Path.Combine(context.DirectoryPath, "ArtworkCache"));
        ManualGameProfileFactory profileFactory = new(
            artworkResolver: artworkResolver);
        GameLibrarySyncService service = new(
            profileFactory,
            TimeProvider.System,
            artworkResolver,
            _ => Task.FromResult(new[] { detectedGame }),
            new(providers: []));

        GameLibrarySyncResult syncResult = await service.SyncAsync(
            context.Store,
            CancellationToken.None);
        ManualGameProfile? updatedProfile =
            await context.Store.FindAsync(
                existingProfile.ProfileId,
                CancellationToken.None);
        GameMetadata? metadata = await context.Store.FindMetadataAsync(
            existingProfile.ProfileId,
            CancellationToken.None);

        Assert.IsNotNull(updatedProfile);
        Assert.IsNotNull(metadata);
        return new(
            syncResult,
            existingProfile,
            updatedProfile,
            storedArtworkPath,
            posterPath,
            heroPath,
            metadata);
    }

    private static void AssertRefreshPersisted(
        ArtworkRefreshOutcome outcome)
    {
        Assert.AreEqual(1, outcome.SyncResult.DetectedCount);
        Assert.AreEqual(0, outcome.SyncResult.AddedCount);
        Assert.AreEqual(1, outcome.SyncResult.UpdatedCount);
        Assert.AreEqual(0, outcome.SyncResult.ErrorCount);
        Assert.AreEqual(
            outcome.ExistingProfile.ProfileId,
            outcome.UpdatedProfile.ProfileId);
        Assert.AreEqual(
            outcome.ExistingProfile.ExecutableSha256,
            outcome.UpdatedProfile.ExecutableSha256);
        Assert.AreEqual(
            outcome.PosterPath,
            outcome.UpdatedProfile.ArtworkPath);
        Assert.AreEqual("Steam", outcome.Metadata.Source);
        Assert.AreEqual("12345", outcome.Metadata.ExternalId);
        Assert.AreEqual(outcome.HeroPath, outcome.Metadata.HeroArtworkPath);
    }

    private static string CreateFile(
        string root,
        string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "The test file directory is unavailable."));
        bool hero = relativePath.Contains("hero", StringComparison.OrdinalIgnoreCase);
        uint width = hero ? 1920u : 600u;
        uint height = hero ? 620u : 900u;
        File.WriteAllBytes(
            path,
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? [0x47, 0x53]
                : CreatePng(width, height));
        return path;
    }

    private static string CreateArtworkFile(
        string root,
        string relativePath,
        uint width,
        uint height)
    {
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CreatePng(width, height));
        return path;
    }

    private static async Task<ManualGameProfile> CreateStoredProfileAsync(
        string executablePath,
        string? posterPath,
        DateTimeOffset now) =>
        new(
            GameProfileId.Create(),
            "Game",
            executablePath,
            await ExecutableFileHasher.ComputeSha256Async(
                executablePath,
                CancellationToken.None),
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now.AddDays(-2),
            now.AddHours(-2),
            posterPath);

    private static GameMetadata CreateStoredMetadata(
        GameProfileId profileId,
        string? heroPath,
        DateTimeOffset now) =>
        new(
            profileId,
            "Steam",
            "12345",
            launcherPath: null,
            lastPlayedAtUtc: now.AddHours(-2),
            totalPlaytimeMinutes: 10,
            heroPath,
            now.AddHours(-1));

    private static DetectedGame CreateSteamDetectedGame(
        string executablePath) =>
        new(
            "Steam",
            "12345",
            "Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);

    private static GameLibrarySyncService CreateSyncService(
        LocalGameArtworkResolver resolver,
        TimeProvider timeProvider,
        DetectedGame detected) =>
        new(
            new(timeProvider, resolver),
            timeProvider,
            resolver,
            _ => Task.FromResult(new[] { detected }),
            new(
                [new CountingMetadataProvider()],
                timeProvider,
                TimeSpan.FromHours(6)));

    private static byte[] CreatePng(uint width, uint height)
    {
        int rowBytes = checked((int)(((width * 3u) + 3u) & ~3u));
        int pixelsBytes = checked(rowBytes * (int)height);
        byte[] bitmap = new byte[checked(54 + pixelsBytes)];
        bitmap[0] = 0x42;
        bitmap[1] = 0x4D;
        WriteInt32(bitmap, 2, bitmap.Length);
        WriteInt32(bitmap, 10, 54);
        WriteInt32(bitmap, 14, 40);
        WriteInt32(bitmap, 18, checked((int)width));
        WriteInt32(bitmap, 22, checked((int)height));
        bitmap[26] = 1;
        bitmap[28] = 24;
        WriteInt32(bitmap, 34, pixelsBytes);
        return bitmap;
    }

    private static void WriteInt32(byte[] destination, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(destination, offset);

    private sealed record ArtworkRefreshOutcome(
        GameLibrarySyncResult SyncResult,
        ManualGameProfile ExistingProfile,
        ManualGameProfile UpdatedProfile,
        string? StoredArtworkPath,
        string PosterPath,
        string HeroPath,
        GameMetadata Metadata);

    private sealed class FixedExecutableArtworkExtractor(
        string artworkPath) : IExecutableArtworkExtractor
    {
        public ValueTask<string?> ExtractAsync(
            string executablePath,
            string cacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(artworkPath);
        }
    }

    private sealed class CountingNullArtworkExtractor :
        IExecutableArtworkExtractor
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<string?> ExtractAsync(
            string executablePath,
            string cacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult<string?>(null);
        }
    }

    private sealed class CountingNotFoundHandler : HttpMessageHandler
    {
        private readonly List<Uri> _requestUris = [];
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal IReadOnlyList<Uri> RequestUris
        {
            get
            {
                lock (_requestUris)
                {
                    return _requestUris.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            lock (_requestUris)
            {
                _requestUris.Add(
                    request.RequestUri
                    ?? throw new InvalidOperationException(
                        "The artwork request URI is missing."));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            });
        }
    }

    private sealed class CountingMetadataProvider : IGameMetadataProvider
    {
        private int _invocationCount;

        public string Source => "Steam";

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        public ValueTask<GameMetadataProviderResult?> ReadAsync(
            ManualGameProfile profile,
            string? externalId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult<GameMetadataProviderResult?>(null);
        }
    }

    private sealed class CountingGameRepository(
        IGameProfileRepository profiles,
        IGameMetadataRepository metadata) :
        IGameProfileRepository,
        IGameMetadataRepository
    {
        private int _metadataUpsertCount;

        internal int MetadataUpsertCount =>
            Volatile.Read(ref _metadataUpsertCount);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            profiles.InitializeAsync(cancellationToken);

        public ValueTask<IReadOnlyList<ManualGameProfile>> ListAsync(
            CancellationToken cancellationToken) =>
            profiles.ListAsync(cancellationToken);

        public ValueTask<ManualGameProfile?> FindAsync(
            GameProfileId profileId,
            CancellationToken cancellationToken) =>
            profiles.FindAsync(profileId, cancellationToken);

        public ValueTask UpsertAsync(
            ManualGameProfile profile,
            CancellationToken cancellationToken) =>
            profiles.UpsertAsync(profile, cancellationToken);

        public ValueTask<bool> DeleteAsync(
            GameProfileId profileId,
            CancellationToken cancellationToken) =>
            profiles.DeleteAsync(profileId, cancellationToken);

        public ValueTask<IReadOnlyList<GameMetadata>> ListMetadataAsync(
            CancellationToken cancellationToken) =>
            metadata.ListMetadataAsync(cancellationToken);

        public ValueTask<GameMetadata?> FindMetadataAsync(
            GameProfileId profileId,
            CancellationToken cancellationToken) =>
            metadata.FindMetadataAsync(profileId, cancellationToken);

        public ValueTask UpsertMetadataAsync(
            GameMetadata value,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _metadataUpsertCount);
            return metadata.UpsertMetadataAsync(value, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) :
        TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

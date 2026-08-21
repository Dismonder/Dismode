using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;
using GameShift.Windows.Processes;
using GameShift.Windows.Profiles;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class GameLibrarySyncServiceTests
{
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
    public async Task UnchangedExecutableHashReplacesDifferentArtworkPath()
    {
        ArtworkRefreshOutcome outcome =
            await RunArtworkRefreshAsync(
                useStoredPoster: true,
                executableIsNewer: false);

        AssertRefreshPersisted(outcome);
        Assert.IsNotNull(outcome.StoredArtworkPath);
        Assert.AreNotEqual(
            outcome.StoredArtworkPath,
            outcome.UpdatedProfile.ArtworkPath);
    }

    [TestMethod]
    public async Task ExistingRobloxTextureArtworkIsReplacedWithExeThumbnail()
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(
            context.DirectoryPath,
            Path.Combine("Roblox", "RobloxPlayerBeta.exe"));
        string invalidArtworkPath = CreateFile(
            context.DirectoryPath,
            Path.Combine(
                "Roblox",
                "content",
                "textures",
                "translateIcon.png"));
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
        string? storedArtworkPath = useStoredPoster
            ? CreateFile(
                steamRoot,
                Path.Combine(
                    "appcache",
                    "librarycache",
                    "12345",
                    "library_600x900.jpg"))
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
            outcome.HeroPath,
            outcome.UpdatedProfile.ArtworkPath);
        Assert.AreEqual("Steam", outcome.Metadata.Source);
        Assert.AreEqual("12345", outcome.Metadata.ExternalId);
        Assert.AreEqual(
            outcome.HeroPath,
            outcome.Metadata.HeroArtworkPath);
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
        File.WriteAllBytes(path, [0x47, 0x53]);
        return path;
    }

    private sealed record ArtworkRefreshOutcome(
        GameLibrarySyncResult SyncResult,
        ManualGameProfile ExistingProfile,
        ManualGameProfile UpdatedProfile,
        string? StoredArtworkPath,
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
}

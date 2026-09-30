using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Profiles;
using Dismode.Windows.Profiles;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class GameMetadataRefreshServiceTests
{
    [TestMethod]
    public async Task RefreshDoesNotCopyPosterIntoHeroMetadata()
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(context.DirectoryPath, "Game.exe");
        string posterPath = CreateFile(context.DirectoryPath, "poster.jpg");
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Game",
            executablePath,
            new string('A', 64),
            context.DirectoryPath,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            posterPath);
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        GameMetadataRefreshService service = new(
            [new FakeMetadataProvider(new(null, null, null, null))]);

        await service.RefreshDetectedAsync(
            context.Store,
            context.Store,
            [new("Steam", "12345", "Game", executablePath, [], 100)],
            CancellationToken.None);
        GameMetadata? metadata = await context.Store.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);

        Assert.IsNotNull(metadata);
        Assert.IsNull(metadata.HeroArtworkPath);
    }

    [TestMethod]
    public async Task StartupHonorsTtlAndCompletedSessionForcesOneRefresh()
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(
            context.DirectoryPath,
            "Game.exe");
        string launcherPath = CreateFile(
            context.DirectoryPath,
            "steam.exe");
        string heroPath = CreateFile(
            context.DirectoryPath,
            "hero.jpg");
        DateTimeOffset now = new(
            2026,
            8,
            2,
            10,
            0,
            0,
            TimeSpan.Zero);
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Game",
            executablePath,
            new string('D', 64),
            context.DirectoryPath,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now.AddDays(-1),
            now.AddDays(-1),
            heroPath);
        await context.Store.UpsertAsync(
            profile,
            CancellationToken.None);
        FakeMetadataProvider provider = new(
            new(
                launcherPath,
                now.AddHours(-1),
                TotalPlaytimeMinutes: 100,
                heroPath));
        MutableTimeProvider timeProvider = new(now);
        GameMetadataRefreshService service = new(
            [provider],
            timeProvider,
            TimeSpan.FromHours(6));
        DetectedGame detected = new(
            "Steam",
            "12345",
            "Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);

        GameMetadataRefreshResult first =
            await service.RefreshDetectedAsync(
                context.Store,
                context.Store,
                [detected],
                CancellationToken.None);
        GameMetadataRefreshResult second =
            await service.RefreshDetectedAsync(
                context.Store,
                context.Store,
                [detected],
                CancellationToken.None);

        Assert.AreEqual(1, first.RefreshedCount);
        Assert.AreEqual(0, first.SkippedFreshCount);
        Assert.AreEqual(0, second.RefreshedCount);
        Assert.AreEqual(1, second.SkippedFreshCount);
        Assert.AreEqual(1, provider.InvocationCount);

        DateTimeOffset sessionStart = now.AddMinutes(1);
        DateTimeOffset sessionEnd = sessionStart.AddMinutes(10);
        timeProvider.Advance(TimeSpan.FromMinutes(11));
        provider.Result = new(
            launcherPath,
            sessionEnd,
            TotalPlaytimeMinutes: 111,
            heroPath);
        Assert.IsTrue(
            await service.RefreshAfterSessionAsync(
                context.Store,
                context.Store,
                profile.ProfileId,
                sessionStart,
                sessionEnd,
                CancellationToken.None));
        GameMetadata? refreshed =
            await context.Store.FindMetadataAsync(
                profile.ProfileId,
                CancellationToken.None);

        Assert.AreEqual(2, provider.InvocationCount);
        Assert.IsNotNull(refreshed);
        Assert.AreEqual("Steam", refreshed.Source);
        Assert.AreEqual("12345", refreshed.ExternalId);
        Assert.AreEqual(sessionEnd, refreshed.LastPlayedAtUtc);
        Assert.AreEqual(111L, refreshed.TotalPlaytimeMinutes);
        Assert.AreEqual(heroPath, refreshed.HeroArtworkPath);
        Assert.AreEqual(
            timeProvider.GetUtcNow(),
            refreshed.LastMetadataRefreshAtUtc);
    }

    [TestMethod]
    public async Task FreshPlaceholderIsEnrichedOnceWhenProviderIsAvailable()
    {
        using UserDataTestContext context = new();
        string executablePath = CreateFile(
            context.DirectoryPath,
            "EpicGame.exe");
        string launcherPath = CreateFile(
            context.DirectoryPath,
            "EpicGamesLauncher.exe");
        string heroPath = CreateFile(
            context.DirectoryPath,
            "epic-hero.jpg");
        DateTimeOffset now = new(
            2026,
            8,
            2,
            12,
            0,
            0,
            TimeSpan.Zero);
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Epic Game",
            executablePath,
            new string('E', 64),
            context.DirectoryPath,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            now.AddDays(-1),
            now.AddDays(-1),
            heroPath);
        await context.Store.UpsertAsync(profile, CancellationToken.None);
        await context.Store.UpsertMetadataAsync(
            new(
                profile.ProfileId,
                "Epic Games",
                "catalog-id",
                launcherPath: null,
                lastPlayedAtUtc: null,
                totalPlaytimeMinutes: 0,
                heroPath,
                now.AddMinutes(-1)),
            CancellationToken.None);
        FakeMetadataProvider provider = new(
            new(
                launcherPath,
                now.AddHours(-2),
                TotalPlaytimeMinutes: null,
                heroPath),
            source: "Epic Games");
        GameMetadataRefreshService service = new(
            [provider],
            new MutableTimeProvider(now),
            TimeSpan.FromHours(6));
        DetectedGame detected = new(
            "Epic Games",
            "catalog-id",
            "Epic Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);

        GameMetadataRefreshResult first =
            await service.RefreshDetectedAsync(
                context.Store,
                context.Store,
                [detected],
                CancellationToken.None);
        GameMetadataRefreshResult second =
            await service.RefreshDetectedAsync(
                context.Store,
                context.Store,
                [detected],
                CancellationToken.None);

        Assert.AreEqual(1, first.RefreshedCount);
        Assert.AreEqual(0, first.SkippedFreshCount);
        Assert.AreEqual(0, second.RefreshedCount);
        Assert.AreEqual(1, second.SkippedFreshCount);
        Assert.AreEqual(1, provider.InvocationCount);
        GameMetadata? enriched = await context.Store.FindMetadataAsync(
            profile.ProfileId,
            CancellationToken.None);
        Assert.IsNotNull(enriched);
        Assert.AreEqual(launcherPath, enriched.LauncherPath);
        Assert.AreEqual(now.AddHours(-2), enriched.LastPlayedAtUtc);
    }

    private static string CreateFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, [0x47, 0x53]);
        return Path.GetFullPath(path);
    }

    private sealed class FakeMetadataProvider(
        GameMetadataProviderResult result,
        string source = "Steam") : IGameMetadataProvider
    {
        private int _invocationCount;

        public string Source { get; } = source;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal GameMetadataProviderResult Result { get; set; } = result;

        public ValueTask<GameMetadataProviderResult?> ReadAsync(
            ManualGameProfile profile,
            string? externalId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult<GameMetadataProviderResult?>(Result);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) :
        TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) =>
            _utcNow = _utcNow.Add(duration);
    }
}

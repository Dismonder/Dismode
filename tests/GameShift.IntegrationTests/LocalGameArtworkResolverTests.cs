using GameShift.Windows.Profiles;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class LocalGameArtworkResolverTests
{
    [TestMethod]
    public async Task SteamWideHeroWinsOverPosterInstallArtworkAndExtractor()
    {
        using ArtworkTestContext context = new();
        _ = context.CreateFile(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_600x900.jpg"));
        string steamArtwork = context.CreateFile(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_hero.jpg"));
        _ = context.CreateFile(
            Path.Combine("Game", "cover.png"));
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(
            GameArtworkSource.SteamLibraryCache,
            artwork.Source);
        Assert.AreEqual(steamArtwork, artwork.LocalPath);
        Assert.AreEqual(0, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task SteamNestedWideHeroWinsOverNestedCapsule()
    {
        using ArtworkTestContext context = new();
        _ = context.CreateFile(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "portrait-hash",
                "library_capsule.jpg"));
        string steamArtwork = context.CreateFile(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "hero-hash",
                "library_hero.jpg"));
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(
            GameArtworkSource.SteamLibraryCache,
            artwork.Source);
        Assert.AreEqual(steamArtwork, artwork.LocalPath);
        Assert.AreEqual(0, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task InstallDirectoryArtworkWinsOverExecutableThumbnail()
    {
        using ArtworkTestContext context = new();
        string localArtwork = context.CreateFile(
            Path.Combine("Game", "Game-cover.png"));
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Epic Games", "catalog-id", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(GameArtworkSource.GameDirectory, artwork.Source);
        Assert.AreEqual(localArtwork, artwork.LocalPath);
        Assert.AreEqual(0, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task ExecutableThumbnailIsTheFinalLocalFallback()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Manual", "Game.exe", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(
            GameArtworkSource.ExecutableThumbnail,
            artwork.Source);
        Assert.AreEqual(extractor.OutputPath, artwork.LocalPath);
        Assert.AreEqual(1, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task RobloxInstallTexturesDoNotReplaceExecutableThumbnail()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(
            Path.Combine("Game", "RobloxPlayerBeta.exe"));
        _ = context.CreateFile(
            Path.Combine(
                "Game",
                "content",
                "textures",
                "translateIcon.png"));
        FakeExecutableArtworkExtractor extractor = new(context);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Roblox", "RobloxPlayer", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(
            GameArtworkSource.ExecutableThumbnail,
            artwork.Source);
        Assert.AreEqual(extractor.OutputPath, artwork.LocalPath);
        Assert.AreEqual(1, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task MissingLocalSourcesProducePlaceholderStateWithoutUrl()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        NullExecutableArtworkExtractor extractor = new();
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: extractor);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Manual", "Game.exe", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.IsFalse(
            LocalGameArtworkResolver.IsSafeArtworkFile(
                "https://example.invalid/cover.png"));
        Assert.IsFalse(
            LocalGameArtworkResolver.IsSafeArtworkFile(
                @"\\example.invalid\share\cover.png"));
    }

    private static DetectedGame CreateGame(
        string source,
        string externalId,
        string executablePath) =>
        new(
            source,
            externalId,
            "Game",
            executablePath,
            LaunchArguments: [],
            Confidence: 100);

    private sealed class FakeExecutableArtworkExtractor(
        ArtworkTestContext context) : IExecutableArtworkExtractor
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal string OutputPath { get; } = context.CreateFile(
            Path.Combine("Generated", "Game.png"));

        public ValueTask<string?> ExtractAsync(
            string executablePath,
            string cacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            return ValueTask.FromResult<string?>(OutputPath);
        }
    }

    private sealed class NullExecutableArtworkExtractor :
        IExecutableArtworkExtractor
    {
        public ValueTask<string?> ExtractAsync(
            string executablePath,
            string cacheDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(null);
        }
    }

    private sealed class ArtworkTestContext : IDisposable
    {
        internal ArtworkTestContext()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "GameShift.ArtworkTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        private string DirectoryPath { get; }

        internal string GetPath(string relativePath) =>
            Path.GetFullPath(Path.Combine(DirectoryPath, relativePath));

        internal string CreateFile(string relativePath)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "The test file directory is unavailable."));
            File.WriteAllBytes(path, [0x47, 0x53, 0x41, 0x52, 0x54]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}

using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using GameShift.Windows.Profiles;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class LocalGameArtworkResolverTests
{
    [TestMethod]
    public async Task SteamCdnAcceptsRealJpegWithMatchingMime()
    {
        using ArtworkTestContext context = new();
        string imagePath = await context.CreateJpegAsync(
            Path.Combine("Source", "image.jpg"),
            width: 600,
            height: 900);
        byte[] image = File.ReadAllBytes(imagePath);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            image);
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor(),
            handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(GameArtworkSource.SteamOfficialCdn, artwork.Source);
        Assert.IsTrue(File.Exists(artwork.LocalPath));
    }

    [TestMethod]
    public async Task SteamCdnRejectsMimeThatDoesNotMatchDecodedContainer()
    {
        using ArtworkTestContext context = new();
        string imagePath = await context.CreateEncodedPngAsync(
            Path.Combine("Source", "image.png"),
            width: 600,
            height: 900);
        byte[] image = File.ReadAllBytes(imagePath);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            image);
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor(),
            handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task SteamCdnRejectsRedirectResponse()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using RedirectResponseHandler handler = new();
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.IsGreaterThan(0, handler.InvocationCount);
    }

    [TestMethod]
    public async Task SteamCdnRejectsSuccessfulResponseFromHostOutsideAllowlist()
    {
        using ArtworkTestContext context = new();
        string imagePath = await context.CreateJpegAsync(
            Path.Combine("Source", "image.jpg"),
            width: 600,
            height: 900);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            File.ReadAllBytes(imagePath),
            effectiveResponseUri: new Uri("https://example.invalid/image.jpg"));
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task SteamCdnRejectsContentLengthAbove32MiB()
    {
        using ArtworkTestContext context = new();
        Directory.CreateDirectory(context.GetPath("Cache"));
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            [],
            contentLength: (32L * 1024 * 1024) + 1);
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.AreEqual(0, Directory.GetFiles(context.GetPath("Cache"), "*.tmp.*").Length);
    }

    [TestMethod]
    public async Task SteamCdnRejectsChunkedBodyAbove32MiB()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using OversizedChunkedResponseHandler handler = new();
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.AreEqual(0, Directory.GetFiles(context.GetPath("Cache"), "*.tmp.*").Length);
    }

    [TestMethod]
    public async Task SteamCdnRejectsCorruptJpegPayload()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            [0xFF, 0xD8, 0xFF, 0xD9]);
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task SteamCdnRejectsImageBelow128Pixels()
    {
        using ArtworkTestContext context = new();
        string imagePath = await context.CreateJpegAsync(
            Path.Combine("Source", "small.jpg"),
            width: 127,
            height: 191);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.OK,
            "image/jpeg",
            File.ReadAllBytes(imagePath));
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task SteamCdnReusesValidRoleCacheWithoutRequest()
    {
        using ArtworkTestContext context = new();
        string cached = await context.CreateJpegAsync(
            Path.Combine("Cache", "steam_12345_poster.jpg"),
            width: 600,
            height: 900);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using StaticResponseHandler handler = new(
            HttpStatusCode.NotFound,
            "image/jpeg",
            []);
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(cached, artwork.LocalPath);
        Assert.AreEqual(0, handler.InvocationCount);
    }

    [TestMethod]
    public async Task SteamCdnPropagatesUserCancellationDuringRequest()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using CancellationTokenSource cancellation = new();
        using UserCancellationResponseHandler handler = new(cancellation);
        LocalGameArtworkResolver resolver = CreateSteamResolver(context, handler);

        try
        {
            _ = await resolver.ResolveAsync(
                CreateGame("Steam", "12345", executable),
                cancellation.Token);
            Assert.Fail("User cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        Assert.AreEqual(1, handler.InvocationCount);
    }

    [TestMethod]
    public async Task SteamCdnInternalTimeoutUsesExecutableFallback()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        using NeverCompletingResponseHandler handler = new();
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            extractor,
            handler,
            networkTimeout: TimeSpan.FromMilliseconds(20));

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(GameArtworkSource.ExecutableThumbnail, artwork.Source);
        Assert.AreEqual(1, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task SteamCdnOfflineFailureUsesExecutableFallback()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(context);
        using OfflineResponseHandler handler = new();
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            extractor,
            handler);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(GameArtworkSource.ExecutableThumbnail, artwork.Source);
        Assert.AreEqual(1, extractor.InvocationCount);
    }

    [TestMethod]
    public async Task XboxShellVisualsResolvesRealPosterAndHeroAttributes()
    {
        using ArtworkTestContext context = new();
        string poster = context.CreatePng(
            Path.Combine("Game", "Resources", "Shell", "poster.png"),
            width: 600,
            height: 900);
        string hero = context.CreatePng(
            Path.Combine("Game", "Resources", "Shell", "hero.png"),
            width: 1920,
            height: 620);
        context.WriteText(
            Path.Combine("Game", "MicrosoftGame.Config"),
            "<Game><ShellVisuals PosterImage=\"Resources\\Shell\\poster.png\" HeroImage=\"Resources\\Shell\\hero.png\" /></Game>");
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtworkSet resolved = await resolver.ResolveSetAsync(
            CreateGame("Xbox", "package-id", executable),
            CancellationToken.None);

        Assert.IsNotNull(resolved.Poster);
        Assert.IsNotNull(resolved.Hero);
        Assert.AreEqual(poster, resolved.Poster.LocalPath);
        Assert.AreEqual(hero, resolved.Hero.LocalPath);
    }

    [TestMethod]
    public async Task XboxShellVisualsRejectsRelativeParentAndAbsolutePaths()
    {
        using ArtworkTestContext context = new();
        string outsidePoster = context.CreatePng(
            Path.Combine("Outside", "poster.png"),
            width: 600,
            height: 900);
        _ = context.CreatePng(
            Path.Combine("Outside", "hero.png"),
            width: 1920,
            height: 620);
        context.WriteText(
            Path.Combine("Game", "MicrosoftGame.Config"),
            $"<Game><ShellVisuals PosterImage=\"..\\Outside\\poster.png\" HeroImage=\"{System.Security.SecurityElement.Escape(outsidePoster)}\" /></Game>");
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtworkSet resolved = await resolver.ResolveSetAsync(
            CreateGame("Xbox", "package-id", executable),
            CancellationToken.None);

        Assert.IsNull(resolved.Poster);
        Assert.IsNull(resolved.Hero);
    }

    [TestMethod]
    public async Task XboxShellVisualsDoesNotTraverseDirectoryReparsePoints()
    {
        using ArtworkTestContext context = new();
        string visualRoot = context.GetPath(Path.Combine("Game", "Resources"));
        Directory.CreateDirectory(visualRoot);
        string outsideDirectory = context.GetPath("Outside");
        _ = context.CreatePng(
            Path.Combine("Outside", "hero.png"),
            width: 1920,
            height: 620);
        string linkPath = Path.Combine(visualRoot, "ExternalLink");
        if (!TryCreateDirectorySymbolicLink(linkPath, outsideDirectory))
        {
            return;
        }

        context.WriteText(
            Path.Combine("Game", "MicrosoftGame.Config"),
            "<Game><ShellVisuals HeroImage=\"Resources\" /></Game>");
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtworkSet resolved = await resolver.ResolveSetAsync(
            CreateGame("Xbox", "package-id", executable),
            CancellationToken.None);

        Assert.IsNull(resolved.Hero);
    }

    [TestMethod]
    public async Task RejectsTruncatedImageHeaderInsteadOfUsingItAsPoster()
    {
        using ArtworkTestContext context = new();
        _ = context.CreateTruncatedPng(
            Path.Combine("Game", "Game-cover.png"),
            width: 600,
            height: 900);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Epic Games", "catalog-id", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task RejectsImageWithValidBitmapMetadataAndCorruptPixelPayload()
    {
        using ArtworkTestContext context = new();
        _ = await context.CreatePngWithCorruptPixelPayloadAsync(
            Path.Combine("Game", "Game-cover.png"),
            width: 600,
            height: 900);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Epic Games", "catalog-id", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
    }

    [TestMethod]
    public async Task ResolveSetAsyncKeepsPortraitPosterAndWideHeroIndependent()
    {
        using ArtworkTestContext context = new();
        string poster = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_600x900.png"),
            width: 600,
            height: 900);
        string hero = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_hero.png"),
            width: 1920,
            height: 620);
        string executable = context.CreateFile(
            Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor());

        GameArtworkSet artwork = await resolver.ResolveSetAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork.Poster);
        Assert.IsNotNull(artwork.Hero);
        Assert.AreEqual(poster, artwork.Poster.LocalPath);
        Assert.AreEqual(hero, artwork.Hero.LocalPath);
        Assert.AreNotEqual(artwork.Poster.LocalPath, artwork.Hero.LocalPath);
    }

    [TestMethod]
    public async Task RoleRankingPrefersCorrectArtworkOverLargerLogoAndScreenshot()
    {
        using ArtworkTestContext context = new();
        string poster = context.CreatePng(
            Path.Combine("Game", "Game-poster.bmp"),
            width: 128,
            height: 192);
        string hero = context.CreatePng(
            Path.Combine("Game", "Game-hero.bmp"),
            width: 192,
            height: 128);
        _ = context.CreatePng(
            Path.Combine("Game", "Game-cover-logo.bmp"),
            width: 1200,
            height: 1800);
        _ = context.CreatePng(
            Path.Combine("Game", "Game-hero-screenshot.bmp"),
            width: 1920,
            height: 620);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor());

        GameArtworkSet artwork = await resolver.ResolveSetAsync(
            CreateGame("Epic Games", "catalog-id", executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork.Poster);
        Assert.IsNotNull(artwork.Hero);
        Assert.AreEqual(poster, artwork.Poster.LocalPath);
        Assert.AreEqual(hero, artwork.Hero.LocalPath);
    }

    [TestMethod]
    public async Task ResolutionUsesOne256NameBudgetAcrossSteamAndGameDirectory()
    {
        using ArtworkTestContext context = new();
        string steamCache = context.GetPath(Path.Combine(
            "Steam",
            "appcache",
            "librarycache",
            "12345"));
        Directory.CreateDirectory(steamCache);
        for (int index = 0; index < 200; index++)
        {
            _ = context.CreateFile(Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                $"{index:D4}.dat"));
        }

        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        for (int index = 0; index < 70; index++)
        {
            _ = context.CreateFile(Path.Combine("Game", $"{index:D4}.dat"));
        }

        _ = context.CreatePng(
            Path.Combine("Game", "zzzz-cover.bmp"),
            width: 128,
            height: 192);
        using StaticResponseHandler handler = new(
            HttpStatusCode.NotFound,
            "image/jpeg",
            []);
        RecordingArtworkResolutionObserver observer = new();
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor(),
            handler,
            resolutionObserver: observer);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.AreEqual(256, observer.PosterInspectedNames);
        Assert.IsLessThanOrEqualTo(32, observer.PosterDecodedImages);
    }

    [TestMethod]
    public async Task ResolutionUsesOne32DecodeBudgetAcrossSteamAndGameDirectory()
    {
        using ArtworkTestContext context = new();
        for (int index = 0; index < 20; index++)
        {
            _ = context.CreateSizedFile(Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                $"cover-{index:D3}.bmp"),
                256 * 1024);
        }

        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        for (int index = 0; index < 13; index++)
        {
            _ = context.CreateSizedFile(
                Path.Combine("Game", $"cover-{index:D3}.bmp"),
                256 * 1024);
        }

        _ = context.CreatePng(
            Path.Combine("Game", "cover-999.bmp"),
            width: 128,
            height: 192);
        using StaticResponseHandler handler = new(
            HttpStatusCode.NotFound,
            "image/jpeg",
            []);
        RecordingArtworkResolutionObserver observer = new();
        LocalGameArtworkResolver resolver = new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor(),
            handler,
            resolutionObserver: observer);

        GameArtwork? artwork = await resolver.ResolveAsync(
            CreateGame("Steam", "12345", executable),
            CancellationToken.None);

        Assert.IsNull(artwork);
        Assert.AreEqual(32, observer.PosterDecodedImages);
        Assert.IsLessThanOrEqualTo(256, observer.PosterInspectedNames);
    }

    [TestMethod]
    public async Task CancellationTriggeredDuringEnumerationIsPropagated()
    {
        using ArtworkTestContext context = new();
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        for (int index = 0; index < 20; index++)
        {
            _ = context.CreateFile(Path.Combine("Game", $"{index:D4}.dat"));
        }

        using CancellationTokenSource cancellation = new();
        RecordingArtworkResolutionObserver observer = new(
            cancellation,
            cancelAfterInspectedNames: 5);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor(),
            resolutionObserver: observer);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync(
                CreateGame("Manual", "Game.exe", executable),
                cancellation.Token));
        Assert.AreEqual(5, observer.PosterInspectedNames);
    }

    [TestMethod]
    public async Task ResolveAsyncReturnsSteamPosterOverHeroInstallArtworkAndExtractor()
    {
        using ArtworkTestContext context = new();
        string steamArtwork = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_600x900.jpg"),
            width: 600,
            height: 900);
        _ = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "library_hero.jpg"),
            width: 1920,
            height: 620);
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
    public async Task ResolveAsyncReturnsNestedSteamPosterOverHero()
    {
        using ArtworkTestContext context = new();
        string steamArtwork = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "portrait-hash",
                "library_capsule.jpg"),
            width: 600,
            height: 900);
        _ = context.CreatePng(
            Path.Combine(
                "Steam",
                "appcache",
                "librarycache",
                "12345",
                "hero-hash",
                "library_hero.jpg"),
            width: 1920,
            height: 620);
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
        string localArtwork = context.CreatePng(
            Path.Combine("Game", "Game-cover.png"),
            width: 600,
            height: 900);
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
    public async Task EpicCatalogResolvesExactIdPosterAndHero()
    {
        using ArtworkTestContext context = new();
        const string catalogItemId = "467a7bed47ec44d9b1c9da0c2dae58f7";
        string posterSource = await context.CreateJpegAsync(
            Path.Combine("Source", "poster.jpg"),
            width: 600,
            height: 900);
        string heroSource = await context.CreateJpegAsync(
            Path.Combine("Source", "hero.jpg"),
            width: 1600,
            height: 900);
        byte[] poster = File.ReadAllBytes(posterSource);
        byte[] hero = File.ReadAllBytes(heroSource);
        string catalogPath = context.CreateEpicCatalog(
            catalogItemId,
            ("DieselGameBoxTall", "https://cdn1.epicgames.com/item/game/poster.jpg", 600, 900),
            ("DieselGameBox", "https://cdn1.epicgames.com/item/game/hero.jpg", 1600, 900));
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        using UriArtworkResponseHandler handler = new(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["/item/game/poster.jpg"] = poster,
                ["/item/game/hero.jpg"] = hero,
            });
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: new NullExecutableArtworkExtractor(),
            artworkHttpHandler: handler,
            epicCatalogPaths: [catalogPath]);

        GameArtworkSet artwork = await resolver.ResolveSetAsync(
            CreateGame("Epic Games", catalogItemId, executable),
            CancellationToken.None);

        Assert.IsNotNull(artwork.Poster);
        Assert.IsNotNull(artwork.Hero);
        Assert.AreEqual(
            GameArtworkSource.EpicCatalogCache,
            artwork.Poster.Source);
        Assert.AreEqual(
            GameArtworkSource.EpicCatalogCache,
            artwork.Hero.Source);
        Assert.AreEqual(2, handler.InvocationCount);
    }

    [TestMethod]
    public async Task AntiCheatSplashDoesNotReplaceExecutableHeroFallback()
    {
        using ArtworkTestContext context = new();
        string splashPath = context.CreatePng(
            Path.Combine("Game", "EasyAntiCheat", "SplashScreen.png"),
            width: 1600,
            height: 900);
        string executable = context.CreateFile(Path.Combine("Game", "Game.exe"));
        FakeExecutableArtworkExtractor extractor = new(
            context,
            width: 512,
            height: 512);
        LocalGameArtworkResolver resolver = new(
            steamRoots: [],
            thumbnailCacheDirectory: context.GetPath("Cache"),
            executableExtractor: extractor,
            epicCatalogPaths: []);

        GameArtwork? artwork = await resolver.ResolveRoleAsync(
            CreateGame("Epic Games", "not-a-catalog-id", executable),
            GameArtworkRole.Hero,
            CancellationToken.None);

        Assert.IsNotNull(artwork);
        Assert.AreEqual(
            GameArtworkSource.ExecutableThumbnail,
            artwork.Source);
        Assert.AreEqual(extractor.OutputPath, artwork.LocalPath);
        Assert.IsFalse(
            resolver.IsArtworkValidForRole(
                splashPath,
                GameArtworkRole.Hero,
                CancellationToken.None));
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
        FakeExecutableArtworkExtractor extractor = new(
            context,
            width: 512,
            height: 512);
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

    private static LocalGameArtworkResolver CreateSteamResolver(
        ArtworkTestContext context,
        HttpMessageHandler handler) =>
        new(
            [context.GetPath("Steam")],
            context.GetPath("Cache"),
            new NullExecutableArtworkExtractor(),
            handler);

    private static bool TryCreateDirectorySymbolicLink(
        string linkPath,
        string targetPath)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class FakeExecutableArtworkExtractor(
        ArtworkTestContext context,
        uint width = 600,
        uint height = 900) : IExecutableArtworkExtractor
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        internal string OutputPath { get; } = context.CreatePng(
            Path.Combine("Generated", "Game.bmp"),
            width,
            height);

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

    private sealed class RecordingArtworkResolutionObserver(
        CancellationTokenSource? cancellation = null,
        int cancelAfterInspectedNames = int.MaxValue) :
        IArtworkResolutionObserver
    {
        internal int PosterDecodedImages { get; private set; }

        internal int PosterInspectedNames { get; private set; }

        public void OnBudgetChanged(
            GameArtworkRole role,
            int inspectedNames,
            int decodedImages)
        {
            if (role is not GameArtworkRole.Poster)
            {
                return;
            }

            PosterInspectedNames = inspectedNames;
            PosterDecodedImages = decodedImages;
            if (inspectedNames >= cancelAfterInspectedNames)
            {
                cancellation?.Cancel();
            }
        }
    }

    private sealed class StaticResponseHandler(
        HttpStatusCode statusCode,
        string contentType,
        byte[] payload,
        long? contentLength = null,
        Uri? effectiveResponseUri = null) : HttpMessageHandler
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            if (effectiveResponseUri is not null)
            {
                request.RequestUri = effectiveResponseUri;
            }

            HttpResponseMessage response = new(statusCode)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            if (contentLength is not null)
            {
                response.Content.Headers.ContentLength = contentLength;
            }

            return Task.FromResult(response);
        }
    }

    private sealed class UriArtworkResponseHandler(
        IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!payloads.TryGetValue(path, out byte[]? payload))
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        RequestMessage = request,
                    });
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectResponseHandler : HttpMessageHandler
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _invocationCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([]),
            };
            response.Headers.Location = new Uri("https://example.invalid/image.jpg");
            return Task.FromResult(response);
        }
    }

    private sealed class OversizedChunkedResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(
                    new FixedLengthZeroStream((32L * 1024 * 1024) + 1)),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    private sealed class UserCancellationResponseHandler(
        CancellationTokenSource userCancellation) : HttpMessageHandler
    {
        private int _invocationCount;

        internal int InvocationCount => Volatile.Read(ref _invocationCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            userCancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private sealed class NeverCompletingResponseHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The timeout did not cancel the request.");
        }
    }

    private sealed class OfflineResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(
                new HttpRequestException("Simulated offline transport."));
        }
    }

    private sealed class FixedLengthZeroStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, read);
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..read].Clear();
            _remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [TestMethod]
    public async Task DecoderWaitIsBoundedAndCompletedOperationsPassThrough()
    {
        // PNG z poprawnym naglowkiem i uszkodzonymi pikselami zawiesil
        // GetPixelDataAsync w WIC na zawsze (16.09.2026, 35 min przy 7 s
        // CPU). Limit ma zamienic takie zawieszenie w odrzucony obraz.
        TaskCompletionSource<int> never = new();
        IAsyncOperation<int> stuck = AsyncInfo.Run<int>(cancellation =>
        {
            _ = cancellation.Register(() => never.TrySetCanceled(cancellation));
            return never.Task;
        });

        TimeoutException timeout = await Task.Run(() =>
            Assert.ThrowsExactly<TimeoutException>(() =>
                LocalGameArtworkResolver.WaitBounded(
                    stuck,
                    TimeSpan.FromMilliseconds(200))));
        Assert.Contains("limicie", timeout.Message);

        int value = await Task.Run(() =>
            LocalGameArtworkResolver.WaitBounded(
                AsyncInfo.Run<int>(_ => Task.FromResult(42)),
                TimeSpan.FromSeconds(5)));
        Assert.AreEqual(42, value);
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

        internal string CreateSizedFile(string relativePath, int byteCount)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "The test file directory is unavailable."));
            File.WriteAllBytes(path, new byte[byteCount]);
            return path;
        }

        internal string CreatePng(string relativePath, uint width, uint height)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "The test file directory is unavailable."));
            File.WriteAllBytes(path, CreateBitmap(width, height));
            return path;
        }

        internal Task<string> CreateEncodedPngAsync(
            string relativePath,
            uint width,
            uint height) =>
            CreateEncodedImageAsync(
                relativePath,
                width,
                height,
                BitmapEncoder.PngEncoderId);

        internal Task<string> CreateJpegAsync(
            string relativePath,
            uint width,
            uint height) =>
            CreateEncodedImageAsync(
                relativePath,
                width,
                height,
                BitmapEncoder.JpegEncoderId);

        internal string CreateTruncatedPng(
            string relativePath,
            uint width,
            uint height)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(
                path,
                [
                    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                    0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                    (byte)(width >> 24), (byte)(width >> 16),
                    (byte)(width >> 8), (byte)width,
                    (byte)(height >> 24), (byte)(height >> 16),
                    (byte)(height >> 8), (byte)height,
                    0x08, 0x02, 0x00, 0x00, 0x00,
                ]);
            return path;
        }

        internal async Task<string> CreatePngWithCorruptPixelPayloadAsync(
            string relativePath,
            uint width,
            uint height)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.PngEncoderId,
                stream);
            byte[] pixels = new byte[checked((int)(width * height * 4u))];
            encoder.SetPixelData(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                width,
                height,
                96,
                96,
                pixels);
            await encoder.FlushAsync();

            stream.Seek(0);
            uint byteCount = checked((uint)stream.Size);
            byte[] encoded = new byte[byteCount];
            using DataReader reader = new(stream.GetInputStreamAt(0));
            uint loaded = await reader.LoadAsync(byteCount);
            Assert.AreEqual(byteCount, loaded);
            reader.ReadBytes(encoded);

            File.WriteAllBytes(path, ReplacePngImageDataWithInvalidPayload(encoded));
            return path;
        }

        private async Task<string> CreateEncodedImageAsync(
            string relativePath,
            uint width,
            uint height,
            Guid encoderId)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
                encoderId,
                stream);
            byte[] pixels = new byte[checked((int)(width * height * 4u))];
            encoder.SetPixelData(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Ignore,
                width,
                height,
                96,
                96,
                pixels);
            await encoder.FlushAsync();

            stream.Seek(0);
            uint byteCount = checked((uint)stream.Size);
            byte[] encoded = new byte[byteCount];
            using DataReader reader = new(stream.GetInputStreamAt(0));
            uint loaded = await reader.LoadAsync(byteCount);
            Assert.AreEqual(byteCount, loaded);
            reader.ReadBytes(encoded);
            File.WriteAllBytes(path, encoded);
            return path;
        }

        internal void WriteText(string relativePath, string content)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        internal string CreateEpicCatalog(
            string catalogItemId,
            params (string Type, string Url, int Width, int Height)[] images)
        {
            string json = JsonSerializer.Serialize(
                new[]
                {
                    new
                    {
                        id = catalogItemId,
                        keyImages = images.Select(image => new
                        {
                            type = image.Type,
                            url = image.Url,
                            width = image.Width,
                            height = image.Height,
                        }).ToArray(),
                    },
                });
            string path = GetPath(Path.Combine("Epic", "catcache.bin"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
            return path;
        }

        private static byte[] CreateBitmap(uint width, uint height)
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

        private static int ReadBigEndianInt32(byte[] source, int offset) =>
            (source[offset] << 24)
            | (source[offset + 1] << 16)
            | (source[offset + 2] << 8)
            | source[offset + 3];

        private static byte[] ReplacePngImageDataWithInvalidPayload(
            byte[] encoded)
        {
            using MemoryStream output = new();
            output.Write(encoded, 0, 8);
            int offset = 8;
            bool wroteInvalidIdat = false;
            while (offset <= encoded.Length - 12)
            {
                int chunkLength = ReadBigEndianInt32(encoded, offset);
                int nextChunk = checked(offset + 12 + chunkLength);
                Assert.IsTrue(chunkLength >= 0 && nextChunk <= encoded.Length);
                bool isIdat = encoded[offset + 4] == 0x49
                    && encoded[offset + 5] == 0x44
                    && encoded[offset + 6] == 0x41
                    && encoded[offset + 7] == 0x54;
                if (isIdat)
                {
                    if (!wroteInvalidIdat)
                    {
                        WritePngChunk(
                            output,
                            [0x49, 0x44, 0x41, 0x54],
                            [0x00]);
                        wroteInvalidIdat = true;
                    }
                }
                else
                {
                    output.Write(encoded, offset, nextChunk - offset);
                }

                offset = nextChunk;
            }

            Assert.IsTrue(wroteInvalidIdat);
            Assert.AreEqual(encoded.Length, offset);
            return output.ToArray();
        }

        private static void WritePngChunk(
            Stream output,
            ReadOnlySpan<byte> type,
            ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            WriteBigEndianUInt32(length, checked((uint)data.Length));
            output.Write(length);
            output.Write(type);
            output.Write(data);
            uint crc = UpdateCrc32(uint.MaxValue, type);
            crc = ~UpdateCrc32(crc, data);
            Span<byte> crcBytes = stackalloc byte[4];
            WriteBigEndianUInt32(crcBytes, crc);
            output.Write(crcBytes);
        }

        private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
                }
            }

            return crc;
        }

        private static void WriteBigEndianUInt32(
            Span<byte> destination,
            uint value)
        {
            destination[0] = (byte)(value >> 24);
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
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

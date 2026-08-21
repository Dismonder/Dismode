using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Profiles;
using GameShift.Windows.Profiles;

namespace GameShift.IntegrationTests;

[TestClass]
public sealed class EpicLocalGameMetadataProviderTests
{
    [TestMethod]
    public async Task ReadsNewestMatchingLastPlayedWithoutUsingCredentialFields()
    {
        using EpicMetadataTestContext context = new();
        string executablePath = context.CreateFile("Game.exe");
        string launcherPath = context.CreateFile(
            Path.Combine("Epic", "EpicGamesLauncher.exe"));
        string heroPath = context.CreateFile("hero.jpg");
        string settingsPath = context.WriteText(
            "GameUserSettings.ini",
            """
            [RememberMe]
            Data=super-secret-value
            LastPlayedGame=other:other-catalog:Other,2026-05-12T10:00:00.000Z
            LastPlayedGame=namespace:catalog-id:First,2026-05-11T15:20:34.417Z
            OfflineData=another-secret-value
            LastPlayedGame=namespace:catalog-id:Second,2026-05-13T18:21:11.125Z
            LastPlayedGame=namespace:catalog-id:Malformed,not-a-date
            """);
        ManualGameProfile profile = CreateProfile(
            executablePath,
            heroPath);
        EpicLocalGameMetadataProvider provider = new(
            [settingsPath],
            [launcherPath]);

        GameMetadataProviderResult? result = await provider.ReadAsync(
            profile,
            "catalog-id",
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(launcherPath, result.LauncherPath);
        Assert.AreEqual(
            new DateTimeOffset(
                2026,
                5,
                13,
                18,
                21,
                11,
                125,
                TimeSpan.Zero),
            result.LastPlayedAtUtc);
        Assert.IsNull(result.TotalPlaytimeMinutes);
        Assert.AreEqual(heroPath, result.HeroArtworkPath);
        string exposedValues = string.Join(
            '|',
            result.LauncherPath,
            result.LastPlayedAtUtc,
            result.TotalPlaytimeMinutes,
            result.HeroArtworkPath);
        Assert.DoesNotContain("super-secret-value", exposedValues);
        Assert.DoesNotContain("another-secret-value", exposedValues);
    }

    [TestMethod]
    public async Task IgnoresSettingsFileAboveMaximumSize()
    {
        using EpicMetadataTestContext context = new();
        string executablePath = context.CreateFile("Game.exe");
        string settingsPath = context.GetPath("GameUserSettings.ini");
        await File.WriteAllBytesAsync(
            settingsPath,
            new byte[(4 * 1024 * 1024) + 1]);
        ManualGameProfile profile = CreateProfile(
            executablePath,
            artworkPath: null);
        EpicLocalGameMetadataProvider provider = new(
            [settingsPath],
            launcherPaths: []);

        GameMetadataProviderResult? result = await provider.ReadAsync(
            profile,
            "catalog-id",
            CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DoesNotMatchNamespaceOrAppNameInsteadOfCatalogItemId()
    {
        using EpicMetadataTestContext context = new();
        string executablePath = context.CreateFile("Game.exe");
        string settingsPath = context.WriteText(
            "GameUserSettings.ini",
            """
            LastPlayedGame=catalog-id:different:item,2026-05-13T18:21:11.125Z
            LastPlayedGame=namespace:different:catalog-id,2026-05-14T18:21:11.125Z
            """);
        ManualGameProfile profile = CreateProfile(
            executablePath,
            artworkPath: null);
        EpicLocalGameMetadataProvider provider = new(
            [settingsPath],
            launcherPaths: []);

        GameMetadataProviderResult? result = await provider.ReadAsync(
            profile,
            "catalog-id",
            CancellationToken.None);

        Assert.IsNull(result);
    }

    private static ManualGameProfile CreateProfile(
        string executablePath,
        string? artworkPath) =>
        new(
            GameProfileId.Create(),
            "Epic Test Game",
            executablePath,
            new string('A', 64),
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            artworkPath);

    private sealed class EpicMetadataTestContext : IDisposable
    {
        internal EpicMetadataTestContext()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "GameShift.EpicMetadataTests",
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
            File.WriteAllBytes(path, [0x47, 0x53]);
            return path;
        }

        internal string WriteText(string relativePath, string content)
        {
            string path = GetPath(relativePath);
            File.WriteAllText(path, content);
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

using Dismode.Core.Domain.Identifiers;
using Dismode.Core.Profiles;
using Dismode.Windows.Profiles;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class SteamLocalGameMetadataProviderTests
{
    [TestMethod]
    public async Task ReadsManifestAndMostRecentLocalUsageWithoutArtwork()
    {
        using SteamMetadataTestContext context = new();
        string steamRoot = context.CreateDirectory("Steam");
        string libraryRoot = context.CreateDirectory("Library");
        string executablePath = context.CreateFile(
            Path.Combine(
                "Library",
                "steamapps",
                "common",
                "Test Game",
                "TestGame.exe"));
        string launcherPath = context.CreateFile(
            Path.Combine("Steam", "steam.exe"));
        context.WriteText(
            Path.Combine("Steam", "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path" "{{EscapeVdf(libraryRoot)}}"
                }
            }
            """);
        context.WriteText(
            Path.Combine(
                "Library",
                "steamapps",
                "appmanifest_12345.acf"),
            $$"""
            "AppState"
            {
                "appid" "12345"
                "LauncherPath" "{{EscapeVdf(launcherPath)}}"
                "LastPlayed" "1700000000"
            }
            """);
        context.WriteLocalConfig(
            userId: "100",
            appId: "12345",
            lastPlayed: 1700000100,
            playtime: 40);
        context.WriteLocalConfig(
            userId: "200",
            appId: "12345",
            lastPlayed: 1700000200,
            playtime: 95);
        ManualGameProfile profile = new(
            GameProfileId.Create(),
            "Test Game",
            executablePath,
            new string('A', 64),
            Path.GetDirectoryName(executablePath)!,
            launchArguments: [],
            OptimizationPreset.Balanced,
            isEnabled: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        SteamLocalGameMetadataProvider provider = new([steamRoot]);

        GameMetadataProviderResult? result = await provider.ReadAsync(
            profile,
            "12345",
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(launcherPath, result.LauncherPath);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeSeconds(1700000200),
            result.LastPlayedAtUtc);
        Assert.AreEqual(95L, result.TotalPlaytimeMinutes);
        Assert.IsNull(result.HeroArtworkPath);
    }

    private static string EscapeVdf(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal);

    private sealed class SteamMetadataTestContext : IDisposable
    {
        internal SteamMetadataTestContext()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "Dismode.SteamMetadataTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        private string DirectoryPath { get; }

        internal string GetPath(string relativePath) =>
            Path.GetFullPath(Path.Combine(DirectoryPath, relativePath));

        internal string CreateDirectory(string relativePath)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

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

        internal void WriteText(string relativePath, string content)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "The test metadata directory is unavailable."));
            File.WriteAllText(path, content);
        }

        internal void WriteLocalConfig(
            string userId,
            string appId,
            long lastPlayed,
            long playtime)
        {
            WriteText(
                Path.Combine(
                    "Steam",
                    "userdata",
                    userId,
                    "config",
                    "localconfig.vdf"),
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
                                    "{{appId}}"
                                    {
                                        "LastPlayed" "{{lastPlayed}}"
                                        "Playtime" "{{playtime}}"
                                    }
                                }
                            }
                        }
                    }
                    "Authentication"
                    {
                        "AuthData" "ignored-by-provider"
                    }
                }
                """);
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

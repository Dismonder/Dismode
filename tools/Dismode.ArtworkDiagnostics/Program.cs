using System.Globalization;
using System.Text;
using System.Text.Json;
using Dismode.Windows.Profiles;

if (args.Length != 2 || args[0] is not ("before" or "after"))
{
    Console.Error.WriteLine("Usage: Dismode.ArtworkDiagnostics before|after <verification-directory>");
    return 1;
}

string phase = args[0];
string outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);
using DiagnosticHttpHandler handler = new();
LocalGameArtworkResolver resolver = new(
    thumbnailCacheDirectory: Path.Combine(outputDirectory, "artwork-cache-" + phase),
    artworkHttpHandler: handler);
using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
DetectedGame[] games = await InstalledGameDiscoveryService.DiscoverAsync(cancellation.Token);
StringBuilder report = new();
report.AppendLine(CultureInfo.InvariantCulture, $"{phase.ToUpperInvariant()} | {DateTimeOffset.UtcNow:O} | games={games.Length}");
int missingPoster = 0;
int missingHero = 0;
int iconOnly = 0;
foreach (DetectedGame game in games)
{
    handler.Messages.Clear();
    GameArtworkSet artwork = await resolver.ResolveSetAsync(game, cancellation.Token);
    missingPoster += artwork.Poster is null ? 1 : 0;
    missingHero += artwork.Hero is null ? 1 : 0;
    iconOnly += artwork.Poster?.Source is GameArtworkSource.ExecutableThumbnail
        && artwork.Hero?.Source is GameArtworkSource.ExecutableThumbnail ? 1 : 0;
    string row = $"{game.DisplayName} | {game.Source} | id={game.ExternalId} | "
        + $"Poster={Describe(artwork.Poster)} | Hero={Describe(artwork.Hero)}";
    report.AppendLine(row);
    foreach (string message in handler.Messages)
    {
        report.AppendLine("  HTTP: " + message);
    }
    Console.WriteLine(row);
}

report.AppendLine(CultureInfo.InvariantCulture, $"SUMMARY {phase}: games={games.Length}, missingPoster={missingPoster}, missingHero={missingHero}, iconOnly={iconOnly}");
string epicDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "Epic", "EpicGamesLauncher", "Data");
string manifestsDirectory = Path.Combine(epicDataDirectory, "Manifests");
List<string> manifestIds = [];
if (Directory.Exists(manifestsDirectory))
{
    foreach (string path in Directory.EnumerateFiles(manifestsDirectory, "*.item"))
    {
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation.Token));
        JsonElement item = manifest.RootElement;
        if (!item.TryGetProperty("LaunchExecutable", out JsonElement launch) || string.IsNullOrWhiteSpace(launch.GetString()))
        {
            continue;
        }
        string id = item.GetProperty("CatalogItemId").GetString()!;
        manifestIds.Add(id);
        string executable = Path.Combine(item.GetProperty("InstallLocation").GetString()!, launch.GetString()!);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"MANIFEST {item.GetProperty("DisplayName")} id={id} namespace={item.GetProperty("CatalogNamespace")} executable={executable} exists={File.Exists(executable)}");
        if (!File.Exists(executable))
        {
            // Skaner pomija taki manifest, ale zapisany profil nadal go pokazuje:
            // resolver dostaje identycznosc ze sklepu i probuje bez pliku gry.
            handler.Messages.Clear();
            DetectedGame stale = new(
                "Epic Games",
                id,
                item.GetProperty("DisplayName").GetString() ?? id,
                executable,
                LaunchArguments: [],
                Confidence: 100,
                CatalogNamespace: item.TryGetProperty("CatalogNamespace", out JsonElement ns) ? ns.GetString() : null);
            GameArtworkSet staleArtwork = await resolver.ResolveSetAsync(stale, cancellation.Token);
            report.AppendLine(CultureInfo.InvariantCulture, $"  stale profile: Poster={Describe(staleArtwork.Poster)} | Hero={Describe(staleArtwork.Hero)}");
            foreach (string message in handler.Messages)
            {
                report.AppendLine("  HTTP: " + message);
            }
        }
    }
}
string catalogPath = Path.Combine(epicDataDirectory, "Catalog", "catcache.bin");
if (File.Exists(catalogPath))
{
    string encoded = await File.ReadAllTextAsync(catalogPath, cancellation.Token);
    string json = encoded.TrimStart().StartsWith('[')
        ? encoded
        : Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    using JsonDocument catalog = JsonDocument.Parse(json);
    foreach (JsonElement item in catalog.RootElement.EnumerateArray())
    {
        if (item.TryGetProperty("id", out JsonElement id)
            && manifestIds.Contains(id.GetString()!, StringComparer.OrdinalIgnoreCase))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"CATALOG id={id} namespace={item.GetProperty("namespace")} title={item.GetProperty("title")}");
            foreach (JsonElement image in item.GetProperty("keyImages").EnumerateArray())
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {image.GetProperty("type")} {image.GetProperty("width")}x{image.GetProperty("height")} {image.GetProperty("url")}");
            }
        }
    }
}

string reportPath = Path.Combine(outputDirectory, "artwork-report.txt");
if (phase == "before")
{
    await File.WriteAllTextAsync(reportPath, report.ToString(), cancellation.Token);
}
else
{
    string previous = File.Exists(reportPath)
        ? await File.ReadAllTextAsync(reportPath, cancellation.Token) : string.Empty;
    int previousAfter = previous.IndexOf("AFTER |", StringComparison.Ordinal);
    if (previousAfter >= 0)
    {
        previous = previous[..previousAfter];
    }
    await File.WriteAllTextAsync(reportPath,
        previous.TrimEnd() + Environment.NewLine + Environment.NewLine + report, cancellation.Token);
}
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"SUMMARY {phase}: games={games.Length}, missingPoster={missingPoster}, missingHero={missingHero}, iconOnly={iconOnly}"));
return 0;

static string Describe(GameArtwork? artwork) => artwork is null
    ? "brak (brak lokalnego obrazu zgodnego z rolą, pobranie niedostępne/odrzucone, brak poprawnej miniatury EXE)"
    : $"{artwork.Source}{(artwork.IsIcon ? " (ikona)" : string.Empty)} [{artwork.LocalPath}]";

internal sealed class DiagnosticHttpHandler : DelegatingHandler
{
    internal DiagnosticHttpHandler() : base(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal List<string> Messages { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            Messages.Add($"{request.RequestUri} => {response.StatusCode}, {response.Content.Headers.ContentType}");
            return response;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            Messages.Add($"{request.RequestUri} => {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }
}

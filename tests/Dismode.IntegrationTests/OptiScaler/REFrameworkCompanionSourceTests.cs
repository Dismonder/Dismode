using System.IO.Compression;
using System.Net;
using Dismode.Windows.OptiScaler;

namespace Dismode.IntegrationTests.OptiScaler;

[TestClass]
public sealed class REFrameworkCompanionSourceTests
{
    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "dismode-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void RemoveDirectory()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public async Task ArchiveWithAnUnexpectedHashIsRefused()
    {
        // Wydanie jest przypiete razem z suma kontrolna. Podmieniona paczka
        // — nawet poprawnie zbudowana i zawierajaca wlasciwy plik — nie moze
        // trafic do katalogu gry.
        using HttpClient client = new(
            new StubHandler(BuildArchive("dinput8.dll", "podmieniony")));
        REFrameworkCompanionSource source = new(client);

        CompanionDownloadResult result = await source.DownloadAsync(
            _directory,
            "dinput8.dll",
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.FilePath);
        StringAssert.Contains(result.Message, "Suma kontrolna");
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "dinput8.dll")));
    }

    [TestMethod]
    public async Task FailedDownloadIsReportedRatherThanThrown()
    {
        using HttpClient client = new(
            new StubHandler(null, HttpStatusCode.NotFound));
        REFrameworkCompanionSource source = new(client);

        CompanionDownloadResult result = await source.DownloadAsync(
            _directory,
            "dinput8.dll",
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Message, "404");
    }

    [TestMethod]
    public void PinnedRevisionIsRecorded()
    {
        // Rewizja jest czescia przypiecia; zmiana wydania bez zmiany tej
        // wartosci przechodzilaby niezauwazona.
        Assert.AreEqual(40, REFrameworkCompanionSource.PinnedRevision.Length);
        Assert.IsTrue(REFrameworkCompanionSource.PinnedRevision.All(
            char.IsAsciiHexDigitLower));
    }

    private static byte[] BuildArchive(string entryName, string content)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, true))
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using StreamWriter writer = new(entry.Open());
            writer.Write(content);
        }

        return buffer.ToArray();
    }

    private sealed class StubHandler(
        byte[]? payload,
        HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(payload ?? []),
            });
    }
}

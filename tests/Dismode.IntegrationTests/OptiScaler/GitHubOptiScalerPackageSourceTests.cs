using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dismode.Core.OptiScaler;
using Dismode.Windows.OptiScaler;

namespace Dismode.IntegrationTests.OptiScaler;

[TestClass]
public sealed class GitHubOptiScalerPackageSourceTests
{
    private static readonly byte[] SevenZipPayload =
        Convert.FromBase64String(
            "N3q8ryccAAR97wngCwAAAAAAAABaAAAAAAAAAHNSDhcBAAZEYW1pYW4KAAEEBgABCQsA"
            + "BwsBAAEhIQEADAcACAoBvleymwAABQEZDAAAAAAAAAAAAAAAABETAGgAbwBzAHQAbgBh"
            + "AG0AZQAAABkAFAoBAFGoiPyxON0BFQYBACCApIEAAA==");

    // Prawdziwy, minimalny ZIP z jednym pustym plikiem. Zaczyna sie od
    // PK\x03\x04, czyli innych bajtow magicznych niz 7z.
    private static readonly byte[] ZipPayload =
        Convert.FromBase64String(
            "UEsDBBQAAAAAAAAAJV0Mfn/YBAAAAAQAAAAOAAAAT3B0aVNjYWxlci5kbGx0ZXN0UE"
            + "sBAhQAFAAAAAAAAAAlXQx+f9gEAAAABAAAAA4AAAAAAAAAAAAAAIABAAAAAE9wdGlT"
            + "Y2FsZXIuZGxsUEsFBgAAAAABAAEAPAAAADAAAAAAAA==");

    [TestMethod]
    public void DefaultClientUsesBoundedRequestTimeout()
    {
        GitHubOptiScalerPackageSource source = new();
        FieldInfo clientField = typeof(GitHubOptiScalerPackageSource).GetField(
            "_client",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new AssertFailedException("Nie znaleziono klienta HTTP.");
        HttpClient client = (HttpClient)(clientField.GetValue(source)
            ?? throw new AssertFailedException("Klient HTTP jest pusty."));

        Assert.AreEqual(TimeSpan.FromSeconds(45), client.Timeout);
    }

    [TestMethod]
    [DataRow(
        OptiScalerReleaseChannel.Stable,
        "v0.9.4",
        "/repos/optiscaler/OptiScaler/releases",
        "/optiscaler/OptiScaler/releases/download/")]
    [DataRow(
        OptiScalerReleaseChannel.Beta,
        "0.9.5-pre3",
        "/repos/Optiscaler-Client/Optiscaler-Betas/releases",
        "/Optiscaler-Client/Optiscaler-Betas/releases/download/")]
    [DataRow(
        OptiScalerReleaseChannel.Nightly,
        "nightly-20260904",
        "/repos/optiscaler/OptiScaler-nightly/releases",
        "/optiscaler/OptiScaler-nightly/releases/download/")]
    public async Task ChannelUsesItsPinnedReleaseRepository(
        OptiScalerReleaseChannel channel,
        string tag,
        string expectedApiPath,
        string expectedDownloadPrefix)
    {
        string digest = Convert.ToHexString(SHA256.HashData(SevenZipPayload))
            .ToLowerInvariant();
        string version = tag.TrimStart('v', 'V');
        string metadata = "[" + BuildMetadata(
            digest,
            SevenZipPayload.Length,
            tag,
            expectedDownloadPrefix) + "]";
        using ReleaseHandler handler = new(
            metadata,
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);

        IReadOnlyList<OptiScalerPackageDescriptor> releases =
            await source.GetAvailableAsync(channel, CancellationToken.None);

        Assert.HasCount(1, releases);
        Assert.AreEqual(channel, releases[0].Channel);
        Assert.AreEqual(version, releases[0].Version);
        Assert.AreEqual(expectedApiPath, handler.RequestUris.Single().AbsolutePath);
        StringAssert.StartsWith(
            releases[0].DownloadUri.AbsolutePath,
            expectedDownloadPrefix);
    }

    [TestMethod]
    public async Task OfficialReleaseRedirectAndDigestProduceVerifiedCacheFile()
    {
        string digest = Convert.ToHexString(SHA256.HashData(SevenZipPayload))
            .ToLowerInvariant();
        using ReleaseHandler handler = new(
            BuildMetadata(digest, SevenZipPayload.Length),
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);
        string cache = Path.Combine(
            Path.GetTempPath(),
            "Dismode-OptiScaler-Source-" + Guid.NewGuid().ToString("N"));
        try
        {
            OptiScalerPackageDescriptor descriptor =
                await source.GetLatestAsync(CancellationToken.None);
            string package = await source.DownloadAsync(
                descriptor,
                cache,
                CancellationToken.None);

            Assert.AreEqual("0.9.4", descriptor.Version);
            CollectionAssert.AreEqual(SevenZipPayload, await File.ReadAllBytesAsync(package));
            Assert.AreEqual(3, handler.RequestCount);
        }
        finally
        {
            TryDelete(cache);
        }
    }

    [TestMethod]
    public async Task RedirectOutsideGitHubAllowlistIsRejected()
    {
        string digest = Convert.ToHexString(SHA256.HashData(SevenZipPayload))
            .ToLowerInvariant();
        using ReleaseHandler handler = new(
            BuildMetadata(digest, SevenZipPayload.Length),
            SevenZipPayload,
            new Uri("https://example.invalid/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);
        OptiScalerPackageDescriptor descriptor =
            await source.GetLatestAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.DownloadAsync(
                descriptor,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task RedirectToNonStandardGitHubPortIsRejected()
    {
        string digest = Convert.ToHexString(SHA256.HashData(SevenZipPayload))
            .ToLowerInvariant();
        using ReleaseHandler handler = new(
            BuildMetadata(digest, SevenZipPayload.Length),
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com:444/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);
        OptiScalerPackageDescriptor descriptor =
            await source.GetLatestAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.DownloadAsync(
                descriptor,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task ReleaseWithoutSha256DigestIsRejected()
    {
        using ReleaseHandler handler = new(
            BuildMetadata("not-a-digest", SevenZipPayload.Length),
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.GetLatestAsync(CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task AssetOutsideOfficialRepositoryIsRejected()
    {
        string digest = Convert.ToHexString(SHA256.HashData(SevenZipPayload))
            .ToLowerInvariant();
        string metadata = BuildMetadata(digest, SevenZipPayload.Length).Replace(
            "https://github.com/optiscaler/OptiScaler/releases/download/",
            "https://github.com/untrusted/repository/releases/download/",
            StringComparison.Ordinal);
        using ReleaseHandler handler = new(
            metadata,
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.GetLatestAsync(CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task MalformedReleaseMetadataIsReportedAsInvalidData()
    {
        using ReleaseHandler handler = new(
            "{\"tag_name\":\"v0.9.4\",\"assets\":[{\"name\":\"Optiscaler_0.9.4.7z\"}]}",
            SevenZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await source.GetLatestAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task NeuralRenderingChannelAcceptsAZipPackage()
    {
        // Fork publikuje ZIP, a nie 7z. Zaszycie bajtow 7z odrzucalo poprawna
        // paczke komunikatem "Pobrany plik nie jest archiwum 7z".
        string cache = await DownloadForkPackageAsync(ZipPayload);
        try
        {
            string file = Directory.GetFiles(cache)[0];
            StringAssert.EndsWith(file, ".zip");
            CollectionAssert.AreEqual(ZipPayload, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            TryDelete(cache);
        }
    }

    [TestMethod]
    public async Task NeuralRenderingChannelRejectsASevenZipPackage()
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => TryDelete(await DownloadForkPackageAsync(SevenZipPayload)));
    }

    [TestMethod]
    public async Task OfficialChannelStillRejectsAZipPackage()
    {
        string digest = Convert.ToHexString(SHA256.HashData(ZipPayload))
            .ToLowerInvariant();
        using ReleaseHandler handler = new(
            BuildMetadata(digest, ZipPayload.Length),
            ZipPayload,
            new Uri("https://release-assets.githubusercontent.com/package.7z"));
        GitHubOptiScalerPackageSource source = new(handler);
        string cache = Path.Combine(
            Path.GetTempPath(),
            "Dismode-OptiScaler-Source-" + Guid.NewGuid().ToString("N"));
        OptiScalerPackageDescriptor descriptor = new(
            "0.9.4",
            new Uri(
                "https://github.com/optiscaler/OptiScaler/releases/download/"
                    + "v0.9.4/Optiscaler_0.9.4-final.7z"),
            ZipPayload.Length,
            digest);

        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await source.DownloadAsync(
                    descriptor,
                    cache,
                    CancellationToken.None));
        }
        finally
        {
            TryDelete(cache);
        }
    }

    private static async Task<string> DownloadForkPackageAsync(byte[] payload)
    {
        string digest = Convert.ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        using ReleaseHandler handler = new(
            "{}",
            payload,
            new Uri("https://release-assets.githubusercontent.com/package.zip"));
        GitHubOptiScalerPackageSource source = new(handler);
        string cache = Path.Combine(
            Path.GetTempPath(),
            "Dismode-OptiScaler-Fork-" + Guid.NewGuid().ToString("N"));
        OptiScalerPackageDescriptor descriptor = new(
            "0.2.0-dlssnr",
            new Uri(
                "https://github.com/Dagherbou/OptiScaler_DLSSNR/releases/download/"
                    + "v0.2.0-dlssnr/OptiScaler-DLSSNR-v0.2.0.zip"),
            payload.Length,
            digest,
            OptiScalerReleaseChannel.DlssNeuralRendering);

        await source.DownloadAsync(descriptor, cache, CancellationToken.None);
        return cache;
    }

    private static string BuildMetadata(
        string digest,
        int size,
        string tag = "v0.9.4",
        string downloadPrefix = "/optiscaler/OptiScaler/releases/download/") => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": false,
          "prerelease": {{(tag.StartsWith("nightly-", StringComparison.Ordinal) ? "true" : "false")}},
          "published_at": "2026-09-04T08:33:41Z",
          "assets": [
            {
              "name": "Optiscaler_0.9.4-final.7z",
              "size": {{size}},
              "digest": "sha256:{{digest}}",
              "browser_download_url": "https://github.com{{downloadPrefix}}{{tag}}/Optiscaler_0.9.4-final.7z"
            }
          ]
        }
        """;

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class ReleaseHandler(
        string metadata,
        byte[] payload,
        Uri redirectUri) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("Request URI is missing.");
            RequestUris.Add(uri);
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
                });
            }

            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Redirect)
                {
                    RequestMessage = request,
                };
                redirect.Headers.Location = redirectUri;
                return Task.FromResult(redirect);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload),
            });
        }
    }
}

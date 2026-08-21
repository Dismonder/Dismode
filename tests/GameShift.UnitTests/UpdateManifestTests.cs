using System.Security.Cryptography;
using System.Text;
using GameShift.Core.Updates;

namespace GameShift.UnitTests;

[TestClass]
public sealed class UpdateManifestTests
{
    private static readonly Uri ServiceBaseUri = new(
        "https://updates.example.test/");

    [TestMethod]
    public void SignedManifestRoundTripsAndVerifies()
    {
        using ECDsa signingKey = ECDsa.Create(
            ECCurve.NamedCurves.nistP256);
        SignedUpdateManifest signed = UpdateManifestCodec.Sign(
            CreateManifest(),
            ServiceBaseUri,
            signingKey);
        byte[] publicKey = signingKey.ExportSubjectPublicKeyInfo();

        SignedUpdateManifest verified =
            UpdateManifestCodec.DeserializeAndVerify(
                UpdateManifestCodec.Serialize(signed),
                ServiceBaseUri,
                expectedChannel: "preview",
                trustedKeyId: "test-key",
                publicKey);

        CollectionAssert.AreEqual(
            UpdateManifestCodec.CreateCanonicalPayload(signed),
            UpdateManifestCodec.CreateCanonicalPayload(verified));
        Assert.AreEqual(signed.Signature, verified.Signature);
    }

    [TestMethod]
    public void TamperedManifestIsRejected()
    {
        using ECDsa signingKey = ECDsa.Create(
            ECCurve.NamedCurves.nistP256);
        SignedUpdateManifest signed = UpdateManifestCodec.Sign(
            CreateManifest(),
            ServiceBaseUri,
            signingKey);
        string json = Encoding.UTF8.GetString(
            UpdateManifestCodec.Serialize(signed));
        string tampered = json.Replace(
            "Pierwsza poprawka",
            "Niepodpisana zmiana",
            StringComparison.Ordinal);

        Assert.ThrowsExactly<CryptographicException>(() =>
            UpdateManifestCodec.DeserializeAndVerify(
                Encoding.UTF8.GetBytes(tampered),
                ServiceBaseUri,
                expectedChannel: "preview",
                trustedKeyId: "test-key",
                signingKey.ExportSubjectPublicKeyInfo()));
    }

    [TestMethod]
    public void VersionMismatchedChunkPathIsRejectedBeforeSigning()
    {
        using ECDsa signingKey = ECDsa.Create(
            ECCurve.NamedCurves.nistP256);
        SignedUpdateManifest invalid = CreateManifest() with
        {
            Installer = CreateManifest().Installer with
            {
                Chunks =
                [
                    new(
                        "/v1/packages/0.1.2/"
                        + "GameShift-Setup-0.1.2-win-x64.part-0001.bin",
                        15,
                        new string('A', 64)),
                ],
            },
        };

        Assert.ThrowsExactly<InvalidDataException>(() =>
            UpdateManifestCodec.Sign(
                invalid,
                ServiceBaseUri,
                signingKey));
    }

    [TestMethod]
    public void AutomaticCheckRunsAtMostOncePerThirtyDays()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UpdatePreferences preferences =
            UpdatePreferences.CreateDefault() with
            {
                LastSuccessfulCheckAtUtc = now.AddDays(-29),
            };

        Assert.IsFalse(preferences.IsAutomaticCheckDue(now));
        Assert.IsTrue(
            (preferences with
            {
                LastSuccessfulCheckAtUtc = now.AddDays(-30),
            }).IsAutomaticCheckDue(now));
        Assert.IsFalse(
            (preferences with
            {
                AutomaticChecksEnabled = false,
                LastSuccessfulCheckAtUtc = null,
            }).IsAutomaticCheckDue(now));
    }

    private static SignedUpdateManifest CreateManifest() =>
        new(
            SchemaVersion: UpdateManifestCodec.CurrentSchemaVersion,
            Channel: "preview",
            Version: "0.1.1",
            MinimumSupportedVersion: "0.1.0",
            PublishedAtUtc: DateTimeOffset.UtcNow,
            DisplayName: "GameShift 0.1.1 Technical Preview",
            ReleaseNotes: ["Pierwsza poprawka"],
            Installer: new(
                FileName: "GameShift-Setup-0.1.1-win-x64.exe",
                SizeBytes: 15,
                Sha256: new string('B', 64),
                Chunks:
                [
                    new(
                        "/v1/packages/0.1.1/"
                        + "GameShift-Setup-0.1.1-win-x64.part-0001.bin",
                        10,
                        new string('C', 64)),
                    new(
                        "/v1/packages/0.1.1/"
                        + "GameShift-Setup-0.1.1-win-x64.part-0002.bin",
                        5,
                        new string('D', 64)),
                ]),
            KeyId: "test-key",
            SignatureAlgorithm:
                UpdateManifestCodec.EcdsaP256Sha256Algorithm,
            Signature: string.Empty);
}

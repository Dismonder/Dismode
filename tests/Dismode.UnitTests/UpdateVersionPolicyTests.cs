using System.Security.Cryptography;
using Dismode.Core.Updates;

namespace Dismode.UnitTests;

[TestClass]
public sealed class UpdateVersionPolicyTests
{
    private static readonly int[] ExpectedStateValues = [1, 2, 3, 4, 5, 6];

    [TestMethod]
    public void UpdateCheckStateKeepsPersistedNumericValues()
    {
        int[] values = Enum.GetValues<UpdateCheckState>()
            .Select(state => (int)state)
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedStateValues,
            values);
    }

    [TestMethod]
    public void OlderPreviewChannelIsClassifiedAsAheadWithoutManifest()
    {
        UpdateVersionDecision decision = UpdateVersionPolicy.Classify(
            "0.3.0",
            CreateManifest("0.1.1", "0.1.1"));

        Assert.AreEqual(UpdateCheckState.AheadOfChannel, decision.State);
        Assert.AreEqual(
            "Zainstalowana wersja 0.3.0 jest nowsza niż kanał preview (0.1.1)",
            decision.Message);
        Assert.IsNull(decision.Manifest);
    }

    [TestMethod]
    public void EqualVersionIsUpToDateWithoutManifest()
    {
        UpdateVersionDecision decision = UpdateVersionPolicy.Classify(
            "0.3.0",
            CreateManifest("0.3.0", "0.1.1"));

        Assert.AreEqual(UpdateCheckState.UpToDate, decision.State);
        Assert.AreEqual("Masz aktualną wersję 0.3.0.", decision.Message);
        Assert.IsNull(decision.Manifest);
    }

    [TestMethod]
    public void SupportedClientCanDownloadNewerVersion()
    {
        SignedUpdateManifest manifest = CreateManifest("0.4.0", "0.1.1");

        UpdateVersionDecision decision = UpdateVersionPolicy.Classify(
            "0.3.0",
            manifest);

        Assert.AreEqual(UpdateCheckState.Available, decision.State);
        Assert.AreEqual("Dostępna jest wersja 0.4.0.", decision.Message);
        Assert.AreSame(manifest, decision.Manifest);
    }

    [TestMethod]
    public void UnsupportedClientRequiresManualReinstallationForNewerOffer()
    {
        UpdateVersionDecision decision = UpdateVersionPolicy.Classify(
            "0.3.0",
            CreateManifest("0.4.0", "0.3.1"));

        Assert.AreEqual(
            UpdateCheckState.ManualUpgradeRequired,
            decision.State);
        StringAssert.Contains(decision.Message, "ręcznie przeinstalować");
        StringAssert.Contains(decision.Message, "0.3.1");
        Assert.IsNull(decision.Manifest);
    }

    [TestMethod]
    public void UserCancellationAndInternalTimeoutAreDistinguished()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.IsTrue(UpdateFailurePolicy.IsUserCancellation(
            new OperationCanceledException(cancellation.Token),
            cancellation.Token));
        Assert.IsFalse(UpdateFailurePolicy.IsUserCancellation(
            new TaskCanceledException(),
            CancellationToken.None));
    }

    [TestMethod]
    public void InternalTimeoutAndInvalidSignatureRemainDistinctFailures()
    {
        string timeout = UpdateFailurePolicy.Describe(
            new TaskCanceledException());
        string invalidSignature = UpdateFailurePolicy.Describe(
            new CryptographicException());

        Assert.AreEqual(
            "Serwer aktualizacji nie odpowiedział w wymaganym czasie.",
            timeout);
        Assert.AreEqual(
            "Aktualizacja nie przeszła weryfikacji kryptograficznej.",
            invalidSignature);
        Assert.AreNotEqual(timeout, invalidSignature);
    }

    private static SignedUpdateManifest CreateManifest(
        string version,
        string minimumSupportedVersion) =>
        new(
            SchemaVersion: UpdateManifestCodec.CurrentSchemaVersion,
            Channel: "preview",
            Version: version,
            MinimumSupportedVersion: minimumSupportedVersion,
            PublishedAtUtc: new DateTimeOffset(
                2026,
                8,
                23,
                0,
                0,
                0,
                TimeSpan.Zero),
            DisplayName: $"Dismode {version} Gaming Edition",
            ReleaseNotes: ["Test"],
            Installer: new(
                FileName: $"Dismode-Setup-{version}-win-x64.exe",
                SizeBytes: 1,
                Sha256: new string('A', 64),
                Chunks:
                [
                    new(
                        $"/v1/packages/{version}/"
                        + $"Dismode-Setup-{version}-win-x64.part-0001.bin",
                        1,
                        new string('B', 64)),
                ]),
            KeyId: "test-key",
            SignatureAlgorithm:
                UpdateManifestCodec.EcdsaP256Sha256Algorithm,
            Signature: Convert.ToBase64String(new byte[64]));
}

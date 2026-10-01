using System.Security.Cryptography;
using Dismode.UpdatePublisher;

namespace Dismode.UnitTests;

[TestClass]
public sealed class UpdatePublisherTrustTests
{
    [TestMethod]
    public void NewlyGeneratedKeyCannotReplaceTheInstalledClientsKey()
    {
        using ECDsa unrelatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.ThrowsExactly<CryptographicException>(() =>
            UpdatePublisherTrust.EnsureTrustedPublicKey(unrelatedKey));
    }

    [TestMethod]
    public void OriginalClientPublicKeyPassesTheIdentityCheck()
    {
        using ECDsa trustedKey = ECDsa.Create();
        trustedKey.ImportSubjectPublicKeyInfo(UpdatePublisherTrust.TrustedPublicKey, out int bytesRead);
        Assert.AreEqual(UpdatePublisherTrust.TrustedPublicKey.Length, bytesRead);
        UpdatePublisherTrust.EnsureTrustedPublicKey(trustedKey);
    }
}

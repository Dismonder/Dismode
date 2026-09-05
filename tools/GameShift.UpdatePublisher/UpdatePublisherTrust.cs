using System.Security.Cryptography;
using GameShift.Core.Product;

namespace GameShift.UpdatePublisher;

internal static class UpdatePublisherTrust
{
    internal static byte[] TrustedPublicKey => Convert.FromBase64String(
        ProductInformation.TrustedUpdatePublicKeySubjectPublicKeyInfoBase64);

    internal static void EnsureTrustedPublicKey(ECDsa signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (!CryptographicOperations.FixedTimeEquals(
                signer.ExportSubjectPublicKeyInfo(), TrustedPublicKey))
        {
            throw new CryptographicException(
                "The update signing key does not match the key trusted by installed GameShift clients. " +
                "Use the original signing key; changing the client trust key is not a release fix.");
        }
    }
}

using System.Runtime.InteropServices;

namespace GameShift.Windows.Security;

public sealed partial class AuthenticodeSignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new(
        "00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private readonly HashSet<string> _trustedThumbprints;

    public AuthenticodeSignatureVerifier(
        IEnumerable<string>? trustedThumbprints = null)
    {
        _trustedThumbprints = new(
            (trustedThumbprints ?? [])
                .Select(NormalizeThumbprint)
                .Where(value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    public AuthenticodeVerificationResult Verify(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            return AuthenticodeVerificationResult.Failed(
                "The caller executable no longer exists.");
        }

        (int trustStatus, string? thumbprint) = VerifyFileTrust(fullPath);
        if (trustStatus != 0 || string.IsNullOrWhiteSpace(thumbprint))
        {
            return AuthenticodeVerificationResult.Failed(
                $"WinVerifyTrust rejected the signature (0x{trustStatus:X8}).");
        }

        string normalizedThumbprint = NormalizeThumbprint(thumbprint);
        return new(
            HasValidAuthenticodeSignature: true,
            IsTrustedSigner: _trustedThumbprints.Contains(normalizedThumbprint),
            normalizedThumbprint,
            _trustedThumbprints.Count == 0
                ? "No production signer is configured; mutations remain read-only."
                : _trustedThumbprints.Contains(normalizedThumbprint)
                    ? "The Authenticode signer is trusted."
                    : "The Authenticode signature is valid but its signer is not allow-listed.");
    }

    private static (int Status, string? Thumbprint) VerifyFileTrust(
        string path)
    {
        nint pathPointer = 0;
        nint fileInfoPointer = 0;
        WinTrustData trustData = default;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            WinTrustFileInfo fileInfo = new()
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
                FilePath = pathPointer,
            };
            fileInfoPointer = Marshal.AllocCoTaskMem(
                Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            trustData = new()
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                UiChoice = WinTrustDataUiChoice.None,
                RevocationChecks = WinTrustDataRevocationChecks.WholeChain,
                UnionChoice = WinTrustDataUnionChoice.File,
                FileInfo = fileInfoPointer,
                StateAction = WinTrustDataStateAction.Verify,
                ProviderFlags =
                    WinTrustDataProviderFlags.RevocationCheckChainExcludeRoot,
                UiContext = WinTrustDataUiContext.Execute,
            };
            Guid action = GenericVerifyV2;
            int status = WinVerifyTrust(
                new nint(-1),
                in action,
                ref trustData);
            return status == 0
                ? (status, ReadSignerThumbprint(trustData.StateData))
                : (status, null);
        }
        finally
        {
            if (trustData.StateData != 0)
            {
                trustData.StateAction = WinTrustDataStateAction.Close;
                Guid action = GenericVerifyV2;
                _ = WinVerifyTrust(
                    new nint(-1),
                    in action,
                    ref trustData);
            }

            if (fileInfoPointer != 0)
            {
                Marshal.FreeCoTaskMem(fileInfoPointer);
            }

            if (pathPointer != 0)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static string? ReadSignerThumbprint(nint stateData)
    {
        nint providerData = WTHelperProvDataFromStateData(stateData);
        if (providerData == 0)
        {
            return null;
        }

        nint signerPointer = WTHelperGetProvSignerFromChain(
            providerData,
            signerIndex: 0,
            counterSigner: false,
            counterSignerIndex: 0);
        if (signerPointer == 0)
        {
            return null;
        }

        CryptProviderSigner signer =
            Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
        if (signer.CertificateCount == 0 || signer.CertificateChain == 0)
        {
            return null;
        }

        CryptProviderCertificateHeader certificate =
            Marshal.PtrToStructure<CryptProviderCertificateHeader>(
                signer.CertificateChain);
        if (certificate.CertificateContext == 0)
        {
            return null;
        }

        uint size = 0;
        if (!CertGetCertificateContextProperty(
                certificate.CertificateContext,
                CertificateSha1HashProperty,
                null,
                ref size)
            || size == 0
            || size > 128)
        {
            return null;
        }

        byte[] hash = new byte[size];
        return CertGetCertificateContextProperty(
            certificate.CertificateContext,
            CertificateSha1HashProperty,
            hash,
            ref size)
                ? Convert.ToHexString(hash.AsSpan(0, checked((int)size)))
                : null;
    }

    private static string NormalizeThumbprint(string? thumbprint) =>
        new((thumbprint ?? string.Empty)
            .Where(char.IsAsciiHexDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    private static partial int WinVerifyTrust(
        nint windowHandle,
        in Guid actionId,
        ref WinTrustData trustData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    private static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    private static partial nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [LibraryImport(
        "crypt32.dll",
        EntryPoint = "CertGetCertificateContextProperty",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertGetCertificateContextProperty(
        nint certificateContext,
        uint propertyId,
        [Out] byte[]? data,
        ref uint dataSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        internal uint StructureSize;
        internal nint FilePath;
        internal nint FileHandle;
        internal nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        internal uint StructureSize;
        internal nint PolicyCallbackData;
        internal nint SipClientData;
        internal WinTrustDataUiChoice UiChoice;
        internal WinTrustDataRevocationChecks RevocationChecks;
        internal WinTrustDataUnionChoice UnionChoice;
        internal nint FileInfo;
        internal WinTrustDataStateAction StateAction;
        internal nint StateData;
        internal nint UrlReference;
        internal WinTrustDataProviderFlags ProviderFlags;
        internal WinTrustDataUiContext UiContext;
        internal nint SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        internal uint StructureSize;
        internal System.Runtime.InteropServices.ComTypes.FILETIME VerifyAsOf;
        internal uint CertificateCount;
        internal nint CertificateChain;
        internal uint SignerType;
        internal nint SignerInfo;
        internal uint Error;
        internal uint CounterSignerCount;
        internal nint CounterSigners;
        internal nint ChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificateHeader
    {
        internal uint StructureSize;
        internal nint CertificateContext;
    }

    private enum WinTrustDataUiChoice : uint
    {
        None = 2,
    }

    private enum WinTrustDataRevocationChecks : uint
    {
        WholeChain = 1,
    }

    private enum WinTrustDataUnionChoice : uint
    {
        File = 1,
    }

    private enum WinTrustDataStateAction : uint
    {
        Verify = 1,
        Close = 2,
    }

    [Flags]
    private enum WinTrustDataProviderFlags : uint
    {
        RevocationCheckChainExcludeRoot = 0x00000080,
    }

    private enum WinTrustDataUiContext : uint
    {
        Execute = 0,
    }

    private const uint CertificateSha1HashProperty = 3;
}

public sealed record AuthenticodeVerificationResult(
    bool HasValidAuthenticodeSignature,
    bool IsTrustedSigner,
    string? SignerThumbprint,
    string Details)
{
    public static AuthenticodeVerificationResult Failed(string details) =>
        new(
            HasValidAuthenticodeSignature: false,
            IsTrustedSigner: false,
            SignerThumbprint: null,
            details);
}

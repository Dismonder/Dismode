using GameShift.Windows.Security;

namespace GameShift.Windows.OptiScaler;

public enum NeuralRenderingModelRejection
{
    None = 0,
    NotFound = 1,
    WrongFileName = 2,
    SignatureInvalid = 3,
    SignerNotNvidia = 4,
}

public sealed record NeuralRenderingModelImportResult(
    bool Accepted,
    NeuralRenderingModelRejection Rejection,
    string? SourcePath,
    string Message)
{
    /// <summary>
    /// True when the file is the right file in the right place and only its
    /// provenance is in doubt. Every copy of the model in public circulation
    /// falls here — measured on three independent ones, two patched in place
    /// and one with the signature stripped — so a gate with no way past it
    /// would not protect the user, it would just delete the feature. A missing
    /// or misnamed file is a different matter: overriding that cannot work.
    /// </summary>
    public bool IsOverridable =>
        Rejection is NeuralRenderingModelRejection.SignatureInvalid
            or NeuralRenderingModelRejection.SignerNotNvidia;
}

/// <summary>
/// Accepts an nvngx_dlssnr.dll the user supplies by hand, and only if Windows
/// agrees it is genuinely NVIDIA's.
/// <para>
/// GameShift will not fetch this file itself. It is not in any driver package
/// we could find — measured on an RTX 5070 with driver 616.64, the package
/// installs nvngx.dll, nvngx_dlssg.dll and nvngx_update.exe and nothing else —
/// and it is not in the OptiScaler fork's archive either. The copies that
/// circulate on GitHub do not survive verification: one carries NVIDIA's
/// version strings with no signature at all, and another carries a real NVIDIA
/// signature whose hash no longer matches the file, meaning the binary was
/// changed after NVIDIA signed it. Downloading either one and loading it into
/// the user's games would be handing an unverifiable binary the run of their
/// machine.
/// </para>
/// <para>
/// So the file has to come from the user, and this class is the gate it passes
/// through. Both of those GitHub copies fail it.
/// </para>
/// </summary>
public sealed class NeuralRenderingModelImporter
{
    /// <summary>
    /// Matched against the signer's subject rather than a pinned thumbprint,
    /// so NVIDIA rotating its signing certificate does not lock users out.
    /// Chain validity is WinVerifyTrust's job; this only settles who signed it.
    /// </summary>
    private const string NvidiaOrganisation = "NVIDIA Corporation";

    private readonly AuthenticodeSignatureVerifier _verifier;
    private readonly Func<string, string?> _signerSubjectReader;

    public NeuralRenderingModelImporter(
        AuthenticodeSignatureVerifier? verifier = null,
        Func<string, string?>? signerSubjectReader = null)
    {
        _verifier = verifier ?? new AuthenticodeSignatureVerifier();
        _signerSubjectReader = signerSubjectReader ?? (static _ => null);
    }

    public NeuralRenderingModelImportResult Inspect(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        string fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
        {
            return new(
                false,
                NeuralRenderingModelRejection.NotFound,
                fullPath,
                "Nie ma takiego pliku.");
        }

        string expected = NvidiaDriverStoreProbe.NeuralRenderingModelFileName;
        if (!string.Equals(
                Path.GetFileName(fullPath),
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                false,
                NeuralRenderingModelRejection.WrongFileName,
                fullPath,
                $"Plik musi nazywać się {expected}. OptiScaler szuka go pod "
                    + "tą nazwą i nie znajdzie go pod żadną inną.");
        }

        AuthenticodeVerificationResult signature = _verifier.Verify(fullPath);
        if (!signature.HasValidAuthenticodeSignature)
        {
            return new(
                false,
                NeuralRenderingModelRejection.SignatureInvalid,
                fullPath,
                "Windows odrzucił podpis tego pliku, więc nie da się "
                    + "stwierdzić, że to naprawdę kod NVIDII. Tak wygląda "
                    + "zarówno plik bez podpisu, jak i taki, który zmieniono "
                    + "po podpisaniu. Nie wstrzykniemy go do gry. "
                    + signature.Details);
        }

        string? subject = signature.SignerSubject ?? _signerSubjectReader(fullPath);
        if (subject is null
            || !subject.Contains(
                NvidiaOrganisation,
                StringComparison.OrdinalIgnoreCase))
        {
            return new(
                false,
                NeuralRenderingModelRejection.SignerNotNvidia,
                fullPath,
                "Podpis jest poprawny, ale nie należy do NVIDIA Corporation"
                    + (subject is null ? "." : $" (podpisał: {subject})."));
        }

        return new(
            true,
            NeuralRenderingModelRejection.None,
            fullPath,
            "Podpis NVIDIA Corporation potwierdzony przez Windows.");
    }
}

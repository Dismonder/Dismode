using Dismode.Windows.Security;

namespace Dismode.Windows.OptiScaler;

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
    string Message,
    string? FileName = null)
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
/// Accepts the NGX libraries the user supplies by hand, and only if Windows
/// agrees they are genuinely NVIDIA's.
/// <para>
/// Dismode will not fetch these files itself. Neither is in any driver
/// package we could find — measured on an RTX 5070 with driver 616.64, the
/// package installs nvngx.dll, nvngx_dlssg.dll and nvngx_update.exe and
/// nothing else — and neither is in the OptiScaler fork's archive. The copies
/// that circulate on GitHub do not survive verification: one carries NVIDIA's
/// version strings with no signature at all, and another carries a real NVIDIA
/// signature whose hash no longer matches the file, meaning the binary was
/// changed after NVIDIA signed it. Downloading either one and loading it into
/// the user's games would be handing an unverifiable binary the run of their
/// machine.
/// </para>
/// <para>
/// So the files have to come from the user, and this class is the gate they
/// pass through. Both of those GitHub copies fail it.
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

    /// <summary>
    /// The two names OptiScaler loads: the Neural Rendering model and the DLSS
    /// runtime it rides on. Enabling the dlss upscaler without the second one
    /// leaves the game with a setting and nothing behind it, so a driver that
    /// ships neither — 616.x ships neither — means both come from the user.
    /// </summary>
    public static IReadOnlyList<string> AcceptedFileNames =>
    [
        NvidiaDriverStoreProbe.NeuralRenderingModelFileName,
        NvidiaDriverStoreProbe.UpscalerModelFileName,
    ];

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

        string? fileName = AcceptedFileNames.FirstOrDefault(
            candidate => string.Equals(
                Path.GetFileName(fullPath),
                candidate,
                StringComparison.OrdinalIgnoreCase));
        if (fileName is null)
        {
            return new(
                false,
                NeuralRenderingModelRejection.WrongFileName,
                fullPath,
                "Plik musi nazywać się "
                    + string.Join(" albo ", AcceptedFileNames)
                    + ". OptiScaler szuka ich pod tymi nazwami i nie znajdzie "
                    + "ich pod żadną inną.");
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
                    + signature.Details,
                fileName);
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
                    + (subject is null ? "." : $" (podpisał: {subject})."),
                fileName);
        }

        return new(
            true,
            NeuralRenderingModelRejection.None,
            fullPath,
            "Podpis NVIDIA Corporation potwierdzony przez Windows.",
            fileName);
    }
}

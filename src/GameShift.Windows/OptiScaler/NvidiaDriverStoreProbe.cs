using GameShift.Core.OptiScaler;
using GameShift.Windows.Security;
using Microsoft.Win32;

namespace GameShift.Windows.OptiScaler;

public sealed record NvidiaModelFile(string FileName, string FullPath);

public sealed record NvidiaDriverStoreSnapshot(
    NvidiaGpuCapability Capability,
    string? ModelDirectory,
    IReadOnlyList<NvidiaModelFile> ModelFiles)
{
    public NvidiaModelFile? Find(string fileName) =>
        ModelFiles.FirstOrDefault(file => string.Equals(
            file.FileName,
            fileName,
            StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Locates the DLSS model files that ship with the installed NVIDIA driver.
/// The files stay in the DriverStore, which only TrustedInstaller can write,
/// so they are a trustworthy source; GameShift never downloads them.
/// </summary>
public sealed class NvidiaDriverStoreProbe
{
    public const string NeuralRenderingModelFileName = "nvngx_dlssnr.dll";

    /// <summary>
    /// The DLSS upscaler runtime itself. Neural Rendering rides on top of it:
    /// switching OptiScaler to the dlss upscaler is pointless if the game
    /// directory has nothing to load under this name.
    /// </summary>
    public const string UpscalerModelFileName = "nvngx_dlss.dll";

    private const string DisplayAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\"
        + "{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] ModelFileNames =
    [
        UpscalerModelFileName,
        "nvngx_dlssd.dll",
        "nvngx_dlssg.dll",
        NeuralRenderingModelFileName,
    ];

    private readonly string _driverStoreRoot;
    private readonly AuthenticodeSignatureVerifier _signatureVerifier;
    private readonly Func<(string? Description, string? DriverVersion)>
        _adapterReader;

    public NvidiaDriverStoreProbe(
        string? driverStoreRoot = null,
        AuthenticodeSignatureVerifier? signatureVerifier = null,
        Func<(string? Description, string? DriverVersion)>? adapterReader = null)
    {
        _driverStoreRoot = driverStoreRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository");
        _signatureVerifier = signatureVerifier ?? new AuthenticodeSignatureVerifier();
        _adapterReader = adapterReader ?? ReadGeForceAdapterFromRegistry;
    }

    public NvidiaDriverStoreSnapshot Probe()
    {
        (string? description, string? driverVersion) = _adapterReader();
        GeForceGeneration generation =
            GeForceGenerationParser.Parse(description);
        NvidiaDriverVersion? version =
            NvidiaDriverVersion.TryFromWindowsDriverVersion(
                driverVersion,
                out NvidiaDriverVersion parsed)
                ? parsed
                : null;

        (string? directory, IReadOnlyList<NvidiaModelFile> files) =
            FindModelFiles();

        bool modelAvailable = files.Any(file => string.Equals(
            file.FileName,
            NeuralRenderingModelFileName,
            StringComparison.OrdinalIgnoreCase));

        return new(
            new(
                IsNvidiaGeForce: generation != GeForceGeneration.Unknown,
                generation,
                version,
                modelAvailable),
            directory,
            files);
    }

    private (string? Directory, IReadOnlyList<NvidiaModelFile> Files)
        FindModelFiles()
    {
        string? bestDirectory = null;
        List<NvidiaModelFile> bestFiles = [];
        foreach (string candidate in EnumerateCandidateDirectories())
        {
            List<NvidiaModelFile> files = [];
            foreach (string fileName in ModelFileNames)
            {
                string path = Path.Combine(candidate, fileName);
                if (File.Exists(path) && HasValidSignature(path))
                {
                    files.Add(new(fileName, path));
                }
            }

            // Several driver revisions can coexist; keep the most complete set.
            if (files.Count > bestFiles.Count)
            {
                bestDirectory = candidate;
                bestFiles = files;
            }
        }

        return (bestDirectory, bestFiles);
    }

    /// <summary>
    /// Directories a driver may lay the NGX model files down in. The
    /// DriverStore revision folders are the documented home, but measured on
    /// an RTX 5070 with driver 616.64 the only model the package installs —
    /// nvngx_dlssg.dll — also sits under Program Files\NVIDIA Corporation, so
    /// looking in one place alone under-reports what the machine has.
    /// </summary>
    private IEnumerable<string> EnumerateCandidateDirectories()
    {
        foreach (string candidate in SafeEnumerateDirectories(
            _driverStoreRoot,
            "nv_dispi.inf_amd64_*"))
        {
            yield return candidate;
        }

        string nvidiaRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation");
        if (!Directory.Exists(nvidiaRoot))
        {
            yield break;
        }

        yield return nvidiaRoot;
        foreach (string candidate in SafeEnumerateDirectories(nvidiaRoot, "*"))
        {
            yield return candidate;
        }
    }

    private static IReadOnlyList<string> SafeEnumerateDirectories(
        string root,
        string pattern)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateDirectories(root, pattern)];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool HasValidSignature(string path)
    {
        try
        {
            return _signatureVerifier.Verify(path)
                .HasValidAuthenticodeSignature;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static (string? Description, string? DriverVersion)
        ReadGeForceAdapterFromRegistry()
    {
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(
                DisplayAdapterClassKey);
            if (classKey is null)
            {
                return (null, null);
            }

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                if (subKeyName.Length != 4
                    || !subKeyName.All(char.IsAsciiDigit))
                {
                    continue;
                }

                using RegistryKey? adapter = classKey.OpenSubKey(subKeyName);
                if (adapter?.GetValue("DriverDesc") is not string description
                    || GeForceGenerationParser.Parse(description)
                        == GeForceGeneration.Unknown)
                {
                    continue;
                }

                return (
                    description,
                    adapter.GetValue("DriverVersion") as string);
            }

            return (null, null);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return (null, null);
        }
    }
}

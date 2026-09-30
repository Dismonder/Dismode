namespace Dismode.Core.OptiScaler;

public enum GeForceGeneration
{
    Unknown = 0,
    PreRtx = 1,
    Rtx20 = 2,
    Rtx30 = 3,
    Rtx40 = 4,
    Rtx50 = 5,
}

/// <summary>
/// NVIDIA branded driver version, for example 616.56. Windows reports a
/// different, four-part number for the same driver.
/// </summary>
public readonly record struct NvidiaDriverVersion(int Major, int Minor)
    : IComparable<NvidiaDriverVersion>
{
    public int CompareTo(NvidiaDriverVersion other) =>
        Major != other.Major
            ? Major.CompareTo(other.Major)
            : Minor.CompareTo(other.Minor);

    public static bool operator <(NvidiaDriverVersion a, NvidiaDriverVersion b) =>
        a.CompareTo(b) < 0;

    public static bool operator >(NvidiaDriverVersion a, NvidiaDriverVersion b) =>
        a.CompareTo(b) > 0;

    public static bool operator <=(NvidiaDriverVersion a, NvidiaDriverVersion b) =>
        a.CompareTo(b) <= 0;

    public static bool operator >=(NvidiaDriverVersion a, NvidiaDriverVersion b) =>
        a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor:D2}";

    /// <summary>
    /// Converts the four-part version reported by Windows, for example
    /// "32.0.15.6636", into the branded NVIDIA version 566.36. The branded
    /// version is the last five digits of the two trailing components.
    /// </summary>
    public static bool TryFromWindowsDriverVersion(
        string? windowsDriverVersion,
        out NvidiaDriverVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(windowsDriverVersion))
        {
            return false;
        }

        string[] parts = windowsDriverVersion.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        string trailing = parts[2] + parts[3];
        if (trailing.Length < 5 || !trailing.All(char.IsAsciiDigit))
        {
            return false;
        }

        string branded = trailing[^5..];
        if (!int.TryParse(branded[..3], out int major)
            || !int.TryParse(branded[3..], out int minor))
        {
            return false;
        }

        version = new(major, minor);
        return true;
    }
}

public static class GeForceGenerationParser
{
    /// <summary>
    /// Derives the GeForce generation from an adapter description such as
    /// "NVIDIA GeForce RTX 5070" or "NVIDIA GeForce RTX 4080 Laptop GPU".
    /// </summary>
    public static GeForceGeneration Parse(string? adapterDescription)
    {
        if (string.IsNullOrWhiteSpace(adapterDescription)
            || adapterDescription.IndexOf(
                "geforce",
                StringComparison.OrdinalIgnoreCase) < 0)
        {
            return GeForceGeneration.Unknown;
        }

        int rtxIndex = adapterDescription.IndexOf(
            "rtx",
            StringComparison.OrdinalIgnoreCase);
        if (rtxIndex < 0)
        {
            return GeForceGeneration.PreRtx;
        }

        ReadOnlySpan<char> tail = adapterDescription.AsSpan(rtxIndex + 3);
        int digitStart = 0;
        while (digitStart < tail.Length && !char.IsAsciiDigit(tail[digitStart]))
        {
            if (!char.IsWhiteSpace(tail[digitStart]))
            {
                return GeForceGeneration.PreRtx;
            }

            digitStart++;
        }

        int digitEnd = digitStart;
        while (digitEnd < tail.Length && char.IsAsciiDigit(tail[digitEnd]))
        {
            digitEnd++;
        }

        ReadOnlySpan<char> model = tail[digitStart..digitEnd];
        if (model.Length != 4)
        {
            return GeForceGeneration.PreRtx;
        }

        // 5 and above collapse to Rtx50 so a future generation satisfies the
        // "RTX 50 or newer" gate instead of being rejected as unknown.
        return model[0] switch
        {
            '2' => GeForceGeneration.Rtx20,
            '3' => GeForceGeneration.Rtx30,
            '4' => GeForceGeneration.Rtx40,
            >= '5' and <= '9' => GeForceGeneration.Rtx50,
            _ => GeForceGeneration.Unknown,
        };
    }
}

public sealed record NvidiaGpuCapability(
    bool IsNvidiaGeForce,
    GeForceGeneration Generation,
    NvidiaDriverVersion? DriverVersion,
    bool NeuralRenderingModelAvailable);

public static class NeuralRenderingPolicy
{
    /// <summary>
    /// First driver branch that ships the DLSS 5 Neural Rendering model.
    /// </summary>
    public static NvidiaDriverVersion MinimumDriverVersion => new(616, 56);

    public static OptiScalerSafetyDecision Evaluate(
        OptiScalerSafetyDecision baseDecision,
        NvidiaGpuCapability capability)
    {
        ArgumentNullException.ThrowIfNull(baseDecision);
        ArgumentNullException.ThrowIfNull(capability);

        if (!baseDecision.CanInstall)
        {
            return baseDecision;
        }

        if (!capability.IsNvidiaGeForce
            || capability.Generation < GeForceGeneration.Rtx50)
        {
            return new(
                false,
                OptiScalerSafetyBlockReason.GpuNotSupported,
                DescribeGeneration(capability));
        }

        if (capability.DriverVersion is not { } driver)
        {
            return new(
                false,
                OptiScalerSafetyBlockReason.DriverTooOld,
                "Nie udało się odczytać wersji sterownika NVIDIA.");
        }

        if (driver < MinimumDriverVersion)
        {
            return new(
                false,
                OptiScalerSafetyBlockReason.DriverTooOld,
                $"Sterownik {driver}; wymagany {MinimumDriverVersion} lub nowszy.");
        }

        return capability.NeuralRenderingModelAvailable
            ? new(true, OptiScalerSafetyBlockReason.None)
            : new(
                false,
                OptiScalerSafetyBlockReason.NeuralRenderingModelMissing,
                "Brak nvngx_dlssnr.dll w magazynie sterowników.");
    }

    private static string DescribeGeneration(NvidiaGpuCapability capability) =>
        capability.IsNvidiaGeForce
            ? $"Wykryto {capability.Generation}; wymagana GeForce RTX 50 lub nowsza."
            : "Neural Rendering wymaga karty GeForce RTX 50 lub nowszej.";
}

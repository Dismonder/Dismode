namespace GameShift.Core.OptiScaler;

public sealed record OptiScalerIniSetting(
    string Section,
    string Key,
    string Value);

public sealed record OptiScalerIniPatchResult(
    string Content,
    IReadOnlyList<OptiScalerIniSetting> Applied,
    IReadOnlyList<OptiScalerIniSetting> NotFound);

/// <summary>
/// Rewrites a closed set of keys in an OptiScaler INI file while leaving every
/// other line, comment and line ending exactly as it was. Keys that the file
/// does not already declare are reported rather than appended, so a package
/// whose layout changed fails loudly instead of getting an unused key.
/// </summary>
public static class OptiScalerIniPatcher
{
    /// <summary>
    /// Settings that switch OptiScaler to DLSS with Neural Rendering.
    /// dlss_12 is the only DX11 upscaler able to drive Neural Rendering.
    /// Model tuning keys are deliberately left alone; they belong to the
    /// in-game overlay.
    /// </summary>
    public static IReadOnlyList<OptiScalerIniSetting> NeuralRenderingSettings =>
    [
        new("Upscalers", "Dx12Upscaler", "dlss"),
        new("Upscalers", "Dx11Upscaler", "dlss_12"),
        new("Upscalers", "VulkanUpscaler", "dlss"),
        new("DLSS", "Enabled", "true"),
        new("DlssNr", "Enabled", "true"),
    ];

    /// <summary>
    /// Refreshes the DirectX 12 Agility SDK that ships inside the package.
    /// Some titles will not run OptiScaler without it, and the package
    /// documents the requirement: the D3D12_OptiScaler folder must sit inside
    /// the OptiScaler folder. The switch is off by default upstream.
    /// </summary>
    public static IReadOnlyList<OptiScalerIniSetting> AgilitySdkSettings =>
    [
        new("FSR", "FsrAgilitySDKUpgrade", "true"),
    ];

    public static OptiScalerIniPatchResult Apply(
        string content,
        IReadOnlyList<OptiScalerIniSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(settings);

        string[] lines = SplitKeepingLineEndings(content);
        List<OptiScalerIniSetting> applied = [];
        HashSet<int> pending = [.. Enumerable.Range(0, settings.Count)];
        string currentSection = string.Empty;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            ReadOnlySpan<char> body = TrimLineEnding(line, out string lineEnding);
            ReadOnlySpan<char> trimmed = body.Trim();

            if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                currentSection = trimmed[1..^1].Trim().ToString();
                continue;
            }

            if (trimmed.Length == 0
                || trimmed[0] is ';' or '#')
            {
                continue;
            }

            int separator = body.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            ReadOnlySpan<char> key = body[..separator].Trim();
            int matchIndex = -1;
            foreach (int settingIndex in pending)
            {
                OptiScalerIniSetting candidate = settings[settingIndex];
                if (key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        currentSection,
                        candidate.Section,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = settingIndex;
                    break;
                }
            }

            if (matchIndex < 0)
            {
                continue;
            }

            OptiScalerIniSetting setting = settings[matchIndex];
            lines[index] =
                string.Concat(body[..separator], "=", setting.Value) + lineEnding;
            applied.Add(setting);
            pending.Remove(matchIndex);
        }

        List<OptiScalerIniSetting> notFound = [.. pending
            .OrderBy(index => index)
            .Select(index => settings[index])];

        return new(string.Concat(lines), applied, notFound);
    }

    private static string[] SplitKeepingLineEndings(string content)
    {
        List<string> lines = [];
        int start = 0;
        for (int index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n')
            {
                continue;
            }

            lines.Add(content[start..(index + 1)]);
            start = index + 1;
        }

        if (start < content.Length)
        {
            lines.Add(content[start..]);
        }

        return [.. lines];
    }

    private static ReadOnlySpan<char> TrimLineEnding(
        string line,
        out string lineEnding)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            lineEnding = "\r\n";
            return line.AsSpan(0, line.Length - 2);
        }

        if (line.EndsWith('\n'))
        {
            lineEnding = "\n";
            return line.AsSpan(0, line.Length - 1);
        }

        lineEnding = string.Empty;
        return line.AsSpan();
    }
}

using System.Collections.ObjectModel;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Profiles;

public sealed record ManualGameProfile
{
    private const int MaximumDisplayNameLength = 120;
    private const int MaximumArgumentCount = 64;
    private const int MaximumArgumentLength = 4096;
    private const int MaximumTotalArgumentLength = 16 * 1024;

    public ManualGameProfile(
        GameProfileId profileId,
        string displayName,
        string executablePath,
        string executableSha256,
        string workingDirectory,
        IEnumerable<string>? launchArguments,
        OptimizationPreset preset,
        bool isEnabled,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        string? artworkPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string normalizedName = displayName.Trim();
        if (normalizedName.Length > MaximumDisplayNameLength
            || normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The profile display name is invalid.",
                nameof(displayName));
        }

        if (!Path.IsPathFullyQualified(executablePath)
            || !string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A manual game profile requires a fully qualified EXE path.",
                nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "The working directory must be fully qualified.",
                nameof(workingDirectory));
        }

        if (executableSha256.Length != 64
            || !executableSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The executable hash must be a SHA-256 value.",
                nameof(executableSha256));
        }

        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset),
                preset,
                "The optimization preset is not recognized.");
        }

        string[] argumentArray = launchArguments?.ToArray() ?? [];
        if (argumentArray.Length > MaximumArgumentCount
            || argumentArray.Any(argument =>
                argument is null || argument.Length > MaximumArgumentLength)
            || argumentArray.Sum(argument => argument.Length)
                > MaximumTotalArgumentLength)
        {
            throw new ArgumentException(
                "The launch arguments exceed the safe profile limits.",
                nameof(launchArguments));
        }

        DateTimeOffset created = createdAtUtc.ToUniversalTime();
        DateTimeOffset updated = updatedAtUtc.ToUniversalTime();
        if (updated < created)
        {
            throw new ArgumentException(
                "A profile cannot be updated before it was created.",
                nameof(updatedAtUtc));
        }

        ProfileId = profileId;
        DisplayName = normalizedName;
        ExecutablePath = Path.GetFullPath(executablePath);
        ExecutableSha256 = executableSha256.ToUpperInvariant();
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        LaunchArguments = new ReadOnlyCollection<string>(argumentArray);
        Preset = preset;
        IsEnabled = isEnabled;
        CreatedAtUtc = created;
        UpdatedAtUtc = updated;
        ArtworkPath = NormalizeArtworkPath(artworkPath);
    }

    public GameProfileId ProfileId { get; }

    public string DisplayName { get; }

    public string ExecutablePath { get; }

    public string ExecutableSha256 { get; }

    public string WorkingDirectory { get; }

    public IReadOnlyList<string> LaunchArguments { get; }

    public OptimizationPreset Preset { get; }

    public bool IsEnabled { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public string? ArtworkPath { get; }

    private static string? NormalizeArtworkPath(string? artworkPath)
    {
        if (string.IsNullOrWhiteSpace(artworkPath))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(artworkPath)
            || artworkPath.StartsWith(@"\\", StringComparison.Ordinal)
            || Uri.TryCreate(
                    artworkPath,
                    UriKind.Absolute,
                    out Uri? artworkUri)
                && (!artworkUri.IsFile || artworkUri.IsUnc)
            || !SupportedArtworkExtensions.Contains(
                Path.GetExtension(artworkPath)))
        {
            throw new ArgumentException(
                "Artwork must be a fully qualified local image path.",
                nameof(artworkPath));
        }

        return Path.GetFullPath(artworkPath);
    }

    private static readonly HashSet<string> SupportedArtworkExtensions = new(
        [".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".webp"],
        StringComparer.OrdinalIgnoreCase);
}

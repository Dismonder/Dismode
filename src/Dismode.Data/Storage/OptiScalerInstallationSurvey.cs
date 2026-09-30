using System.Text.Json;

namespace Dismode.Data.Storage;

/// <summary>
/// Answers one question asked at uninstall time: are there still OptiScaler
/// files sitting inside somebody's game?
/// <para>
/// These installations are not in the recovery journal — that covers system
/// settings, while OptiScaler writes into game directories and tracks each one
/// with its own manifest. Nothing checked them before, so removing Dismode
/// left the mod in every game it had been installed into, together with the
/// backups needed to undo it, and took away the only tool that knew how.
/// </para>
/// </summary>
public static class OptiScalerInstallationSurvey
{
    /// <summary>How many games to name before trailing off.</summary>
    private const int NamedGamesLimit = 5;

    /// <summary>
    /// Describes the outstanding installations, or null when there are none.
    /// Reading is deliberately shallow and forgiving: this runs during an
    /// uninstall, where refusing to answer because one manifest is malformed
    /// would be worse than naming a game imprecisely. A manifest that cannot
    /// be read still counts — the files it describes are still there.
    /// </summary>
    public static string? Describe(string installationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationsDirectory);
        if (!Directory.Exists(installationsDirectory))
        {
            return null;
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(installationsDirectory, "*.json");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (manifests.Length == 0)
        {
            return null;
        }

        List<string> games = [.. manifests
            .Select(manifest => ReadTargetExecutable(manifest)
                ?? "(nieznana gra)")];

        return $"OptiScaler jest nadal zainstalowany w {games.Count} "
            + "katalogach gier: "
            + string.Join(", ", games.Take(NamedGamesLimit))
            + (games.Count > NamedGamesLimit ? ", …" : string.Empty);
    }

    /// <summary>
    /// Describes what is installed under the current user's storage.
    /// </summary>
    public static string? Describe() =>
        Describe(DismodeStoragePaths.OptiScalerInstallationsDirectory);

    private static string? ReadTargetExecutable(string manifestPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty(
                "targetExecutablePath",
                out JsonElement value)
                ? Path.GetFileName(value.GetString())
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            return null;
        }
    }
}

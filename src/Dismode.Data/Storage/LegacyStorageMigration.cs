namespace Dismode.Data.Storage;

/// <summary>
/// Moves data written by releases published under the old name (GameShift)
/// into the Dismode directories. Recovery journals live there, so leaving
/// them behind would silently drop an unfinished restore.
/// </summary>
public static class LegacyStorageMigration
{
    public const string LegacyDirectoryName = "GameShift";

    private static readonly (string Legacy, string Current)[] RenamedFiles =
    [
        ("gameshift-user.db", "dismode-user.db"),
        ("gameshift-user.db-wal", "dismode-user.db-wal"),
        ("gameshift-user.db-shm", "dismode-user.db-shm"),
    ];

    public static IReadOnlyList<string> MigrateUserData() =>
        Migrate(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                LegacyDirectoryName),
            DismodeStoragePaths.UserDataDirectory);

    public static IReadOnlyList<string> MigrateMachineData() =>
        Migrate(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                LegacyDirectoryName),
            DismodeStoragePaths.MachineDataDirectory);

    /// <summary>
    /// Moves the legacy directory in one rename when the new one does not
    /// exist yet; otherwise moves every entry the new directory lacks and
    /// never overwrites anything. Returns the entries it could not move.
    /// </summary>
    public static IReadOnlyList<string> Migrate(
        string legacyDirectory,
        string currentDirectory)
    {
        List<string> problems = [];
        if (!Directory.Exists(legacyDirectory))
        {
            return problems;
        }

        if (!Directory.Exists(currentDirectory))
        {
            string? parent = Path.GetDirectoryName(currentDirectory);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            if (TryMove(legacyDirectory, currentDirectory, problems))
            {
                RenameLegacyFiles(currentDirectory, problems);
                return problems;
            }

            Directory.CreateDirectory(currentDirectory);
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(
            legacyDirectory))
        {
            string target = Path.Combine(
                currentDirectory,
                Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                problems.Add($"{entry}: already exists in {currentDirectory}");
                continue;
            }

            TryMove(entry, target, problems);
        }

        RenameLegacyFiles(currentDirectory, problems);
        TryDeleteIfEmpty(legacyDirectory);
        return problems;
    }

    private static void RenameLegacyFiles(
        string directory,
        List<string> problems)
    {
        foreach ((string legacy, string current) in RenamedFiles)
        {
            string legacyPath = Path.Combine(directory, legacy);
            string currentPath = Path.Combine(directory, current);
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            if (File.Exists(currentPath))
            {
                problems.Add($"{legacyPath}: {current} already exists");
                continue;
            }

            TryMove(legacyPath, currentPath, problems);
        }
    }

    private static bool TryMove(
        string source,
        string target,
        List<string> problems)
    {
        try
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{source}: {exception.Message}");
            return false;
        }
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

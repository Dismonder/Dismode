
namespace Dismode.Data.Storage;

/// <summary>
/// What a migration run left behind.
/// </summary>
/// <param name="Problems">
/// Entries that could not be moved or merged, one line each. Empty when the
/// legacy directory is gone.
/// </param>
/// <param name="BlocksStartup">
/// True when the user database or the recovery journal still sits under its
/// legacy name and the new directory has no copy of its own. Starting now
/// would create an empty database or journal next to the real one, and the
/// user would see a program that forgot everything.
/// </param>
public sealed record LegacyStorageMigrationResult(
    IReadOnlyList<string> Problems,
    bool BlocksStartup)
{
    public static LegacyStorageMigrationResult Empty { get; } =
        new([], BlocksStartup: false);
}

/// <summary>
/// Moves data written by releases published under the old name (GameShift)
/// into the Dismode directories. Recovery journals live there, so leaving
/// them behind would silently drop an unfinished restore.
/// <para>
/// Dismode.UI and Dismode.SessionHost start at the same time and both call
/// this before their first read, so a run is serialized on a named mutex.
/// Nothing is ever overwritten: a file that exists on both sides is dropped
/// only when its content is identical, otherwise it is kept next to the new
/// one under a name that says where it came from. The legacy directory is
/// merged recursively and disappears once it is empty.
/// </para>
/// </summary>
public static class LegacyStorageMigration
{
    public const string LegacyDirectoryName = "GameShift";

    /// <summary>Suffix for a legacy file whose name is already taken.</summary>
    public const string CollisionSuffix = ".gameshift";

    /// <summary>
    /// Suffix for a legacy user database that met a database the new release
    /// had already created. Nobody can merge two SQLite files blindly, so the
    /// legacy one is kept whole and named for what it is.
    /// </summary>
    public const string UnmergedDatabaseInfix = ".unmerged";

    private const string LegacyDatabaseName = "gameshift-user.db";
    private const string CurrentDatabaseName = "dismode-user.db";
    private const string JournalName = "user-recovery.jsonl";
    private const int MoveAttempts = 4;
    private static readonly TimeSpan MoveRetryDelay =
        TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryDelay =
        TimeSpan.FromMilliseconds(100);
    private const string LockSuffix = ".migration.lock";

    // SQLite keeps the write-ahead log and the shared-memory index next to
    // the database under derived names; the three move as one set. The
    // database file goes last: only its name tells the new release the set
    // is complete, so a log left behind by a failed move still blocks the
    // start instead of being silently ignored.
    private static readonly string[] DatabaseSuffixes = ["-shm", "-wal", ""];

    public static LegacyStorageMigrationResult MigrateUserData() =>
        Migrate(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                LegacyDirectoryName),
            DismodeStoragePaths.UserDataDirectory);

    /// <summary>Where releases named GameShift kept the machine data.</summary>
    public static string LegacyMachineDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            LegacyDirectoryName);

    public static LegacyStorageMigrationResult MigrateMachineData() =>
        Migrate(
            LegacyMachineDataDirectory,
            DismodeStoragePaths.MachineDataDirectory);

    /// <summary>
    /// Moves the legacy directory in one rename when the new one does not
    /// exist yet; otherwise merges it entry by entry. Legacy database files
    /// that already landed in the new directory are renamed even when the
    /// legacy directory is gone, so a run interrupted half-way finishes on
    /// the next start instead of leaving the data under a name nobody opens.
    /// </summary>
    public static LegacyStorageMigrationResult Migrate(
        string legacyDirectory,
        string currentDirectory,
        TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        string legacy = Path.GetFullPath(legacyDirectory);
        string current = Path.GetFullPath(currentDirectory);
        if (string.Equals(legacy, current, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The legacy and current directories must differ.",
                nameof(currentDirectory));
        }

        List<string> problems = [];
        TimeSpan timeout = lockTimeout ?? LockTimeout;
        FileStream? gate = TryAcquireGate(current, timeout);
        if (gate is null
            && (Directory.Exists(legacy) || HasLegacyDatabase(current)))
        {
            // Two components moving the same set at once could leave the
            // database and its log under different names. Whoever holds the
            // lock finishes the work; this start is refused instead.
            problems.Add(
                $"{current}: another Dismode component held the migration "
                + $"lock for more than {timeout.TotalSeconds:F0} s; "
                + "start Dismode again.");
            return new(problems, BlocksStartup: true);
        }

        try
        {
            if (!Directory.Exists(legacy)
                && !HasLegacyDatabase(current))
            {
                return problems.Count == 0
                    ? LegacyStorageMigrationResult.Empty
                    : new(problems, BlocksStartup: false);
            }

            if (Directory.Exists(legacy))
            {
                if (!Directory.Exists(current))
                {
                    string? parent = Path.GetDirectoryName(current);
                    if (parent is not null)
                    {
                        Directory.CreateDirectory(parent);
                    }

                    // Wholesale rename first: one atomic step when nothing has
                    // been written under the new name yet. It fails when a
                    // file inside is open, and the merge below then rescues
                    // whatever is not.
                    _ = TryMove(legacy, current, problems, report: false);
                }

                if (Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(current);
                    MergeDirectory(legacy, current, problems);
                    TryDeleteIfEmpty(legacy);
                }
            }

            if (SidecarLeftBehind(legacy)
                && File.Exists(Path.Combine(current, LegacyDatabaseName)))
            {
                // The log or the index could not leave the old directory yet.
                // Renaming the database now would send the two halves under
                // different names on the next start, so the set waits whole.
                problems.Add(
                    $"{Path.Combine(current, LegacyDatabaseName)}: waits for "
                    + $"its journal still held in {legacy}.");
                return new(
                    problems,
                    BlocksStartup: LeavesCriticalDataBehind(legacy, current));
            }

            RenameLegacyDatabase(current, problems);
            return new(
                problems,
                BlocksStartup: LeavesCriticalDataBehind(legacy, current));
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private static void MergeDirectory(
        string source,
        string target,
        List<string> problems)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(source);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{source}: {exception.Message}");
            return;
        }

        foreach (string entry in entries)
        {
            string destination = Path.Combine(target, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (File.Exists(destination))
                {
                    problems.Add(
                        $"{entry}: a file of that name exists in {target}");
                    continue;
                }

                if (!Directory.Exists(destination)
                    && TryMove(entry, destination, problems, report: false))
                {
                    continue;
                }

                // The directory exists on both sides, or it would not move as
                // a whole because something inside is open: merge its
                // children and take the unlocked ones out.
                Directory.CreateDirectory(destination);
                MergeDirectory(entry, destination, problems);
                TryDeleteIfEmpty(entry);
                continue;
            }

            if (Directory.Exists(destination))
            {
                problems.Add(
                    $"{entry}: a directory of that name exists in {target}");
                continue;
            }

            if (File.Exists(destination))
            {
                if (HaveIdenticalContent(entry, destination))
                {
                    TryDelete(entry, problems);
                    continue;
                }

                string aside = FindFreeName(
                    target,
                    Path.GetFileNameWithoutExtension(entry) + CollisionSuffix,
                    Path.GetExtension(entry));
                if (TryMove(entry, aside, problems, report: true))
                {
                    problems.Add(
                        $"{entry}: {Path.GetFileName(destination)} already "
                        + $"existed in {target}; the legacy copy is kept as "
                        + Path.GetFileName(aside));
                }

                continue;
            }

            _ = TryMove(entry, destination, problems, report: true);
        }
    }

    /// <summary>
    /// The database is the only file whose name carries the product name.
    /// A legacy database found in the new directory is renamed with its
    /// journal and index; when the new release already has a database of
    /// its own, the legacy set is kept whole under a name that says so.
    /// </summary>
    private static void RenameLegacyDatabase(
        string directory,
        List<string> problems)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        bool currentExists = File.Exists(
            Path.Combine(directory, CurrentDatabaseName));
        // One base name for the whole set, so the journal and the index keep
        // belonging to the database they were written for.
        string? unmergedBase = currentExists
            ? FindFreeName(
                directory,
                Path.GetFileNameWithoutExtension(LegacyDatabaseName)
                    + UnmergedDatabaseInfix,
                Path.GetExtension(LegacyDatabaseName))
            : null;
        bool reportedCollision = false;
        foreach (string suffix in DatabaseSuffixes)
        {
            string legacyPath = Path.Combine(
                directory,
                LegacyDatabaseName + suffix);
            if (!File.Exists(legacyPath))
            {
                continue;
            }

            if (unmergedBase is not null)
            {
                string unmerged = unmergedBase + suffix;
                if (!TryMove(legacyPath, unmerged, problems, report: true))
                {
                    // The set stays whole under the old name until every
                    // piece can go; the next start picks one name for all.
                    return;
                }

                if (!reportedCollision)
                {
                    reportedCollision = true;
                    problems.Add(
                        $"{legacyPath}: {CurrentDatabaseName} already exists; "
                        + "the legacy database is kept as "
                        + Path.GetFileName(unmerged));
                }

                continue;
            }

            string currentPath = Path.Combine(
                directory,
                CurrentDatabaseName + suffix);
            if (File.Exists(currentPath))
            {
                // Only the journal or the index survived under both names:
                // something opened the new database between two halves of
                // a rename. The legacy piece is not thrown away, and the
                // database stays with it under the old name.
                problems.Add(
                    $"{legacyPath}: {Path.GetFileName(currentPath)} already exists");
                return;
            }

            if (!TryMove(legacyPath, currentPath, problems, report: true))
            {
                return;
            }
        }
    }

    private static bool HasLegacyDatabase(string directory) =>
        Directory.Exists(directory)
        && DatabaseSuffixes.Any(suffix => File.Exists(
            Path.Combine(directory, LegacyDatabaseName + suffix)));

    /// <summary>
    /// True when the write-ahead log or the shared-memory index of the legacy
    /// database is still in the legacy directory.
    /// </summary>
    private static bool SidecarLeftBehind(string legacy) =>
        DatabaseSuffixes
            .Where(suffix => suffix.Length > 0)
            .Any(suffix => File.Exists(
                Path.Combine(legacy, LegacyDatabaseName + suffix)));

    /// <summary>
    /// True when a legacy database or journal is still where the new release
    /// will not look, and nothing of that kind exists under the new name yet.
    /// </summary>
    private static bool LeavesCriticalDataBehind(
        string legacy,
        string current)
    {
        bool databaseBehind =
            (File.Exists(Path.Combine(legacy, LegacyDatabaseName))
                || File.Exists(Path.Combine(current, LegacyDatabaseName)))
            && !File.Exists(Path.Combine(current, CurrentDatabaseName));
        // Rows committed into the write-ahead log live only there until a
        // checkpoint; a database opened without its log would hide them for
        // good, so a legacy log left under either directory blocks the start.
        bool walBehind =
            File.Exists(Path.Combine(legacy, LegacyDatabaseName + "-wal"))
            || File.Exists(Path.Combine(current, LegacyDatabaseName + "-wal"));
        bool journalBehind =
            File.Exists(Path.Combine(legacy, JournalName))
            && !File.Exists(Path.Combine(current, JournalName));
        return databaseBehind || walBehind || journalBehind;
    }

    private static bool TryMove(
        string source,
        string target,
        List<string> problems,
        bool report)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= MoveAttempts; attempt++)
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
                last = exception;
            }

            if (attempt < MoveAttempts)
            {
                // Antivirus and the search indexer open freshly written
                // files for a moment; a running legacy component holds its
                // files for good. A short retry tells the two apart.
                Thread.Sleep(MoveRetryDelay);
            }
        }

        if (report && last is not null)
        {
            problems.Add($"{source}: {last.Message}");
        }

        return false;
    }

    private static void TryDelete(string path, List<string> problems)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: {exception.Message}");
        }
    }

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool HaveIdenticalContent(string first, string second)
    {
        try
        {
            FileInfo firstInfo = new(first);
            FileInfo secondInfo = new(second);
            if (firstInfo.Length != secondInfo.Length)
            {
                return false;
            }

            using FileStream firstStream = new(
                first,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using FileStream secondStream = new(
                second,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            byte[] firstBuffer = new byte[64 * 1024];
            byte[] secondBuffer = new byte[64 * 1024];
            while (true)
            {
                int firstRead = firstStream.ReadAtLeast(
                    firstBuffer,
                    firstBuffer.Length,
                    throwOnEndOfStream: false);
                int secondRead = secondStream.ReadAtLeast(
                    secondBuffer,
                    secondBuffer.Length,
                    throwOnEndOfStream: false);
                if (firstRead != secondRead
                    || !firstBuffer.AsSpan(0, firstRead).SequenceEqual(
                        secondBuffer.AsSpan(0, secondRead)))
                {
                    return false;
                }

                if (firstRead == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FindFreeName(
        string directory,
        string stem,
        string extension)
    {
        string candidate = Path.Combine(directory, stem + extension);
        for (int counter = 2; File.Exists(candidate) || Directory.Exists(candidate); counter++)
        {
            candidate = Path.Combine(directory, $"{stem}-{counter}{extension}");
        }

        return candidate;
    }

    /// <summary>
    /// Exclusive hold on a lock file beside the current directory, or null
    /// when another component kept it for the whole timeout. A file rather
    /// than a named mutex: a mutex created by the elevated host carries a
    /// DACL the unelevated window cannot open, so between those two it
    /// serialised nothing; the directory's own ACL admits both. The file
    /// deletes itself when the holder closes it, also when the holder dies.
    /// </summary>
    private static FileStream? TryAcquireGate(
        string currentDirectory,
        TimeSpan timeout)
    {
        string lockPath = currentDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + LockSuffix;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                string? parent = Path.GetDirectoryName(lockPath);
                if (parent is not null)
                {
                    Directory.CreateDirectory(parent);
                }

                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Held by the other component, or just released and still
                // marked for deletion, which Windows reports as access denied.
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return null;
                }

                Thread.Sleep(LockRetryDelay);
            }
        }
    }
}

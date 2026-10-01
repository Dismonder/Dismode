using System.Security.Cryptography;
using System.Text;

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

    public static LegacyStorageMigrationResult MigrateMachineData() =>
        Migrate(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                LegacyDirectoryName),
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
        string currentDirectory)
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
        using Mutex? mutex = TryOpenMutex(current);
        bool acquired = mutex is not null && AcquireMutex(mutex);
        if (mutex is not null && !acquired)
        {
            problems.Add(
                $"{current}: another Dismode component held the migration "
                + $"lock for more than {LockTimeout.TotalSeconds:F0} s; "
                + "continuing without it.");
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

            RenameLegacyDatabase(current, problems);
            return new(
                problems,
                BlocksStartup: LeavesCriticalDataBehind(legacy, current));
        }
        finally
        {
            if (acquired)
            {
                mutex!.ReleaseMutex();
            }
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
                if (TryMove(legacyPath, unmerged, problems, report: true)
                    && !reportedCollision)
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
                // a rename. The legacy piece is not thrown away.
                problems.Add(
                    $"{legacyPath}: {Path.GetFileName(currentPath)} already exists");
                continue;
            }

            _ = TryMove(legacyPath, currentPath, problems, report: true);
        }
    }

    private static bool HasLegacyDatabase(string directory) =>
        Directory.Exists(directory)
        && DatabaseSuffixes.Any(suffix => File.Exists(
            Path.Combine(directory, LegacyDatabaseName + suffix)));

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

    private static Mutex? TryOpenMutex(string currentDirectory)
    {
        try
        {
            return new Mutex(
                initiallyOwned: false,
                BuildMutexName(currentDirectory));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or WaitHandleCannotBeOpenedException)
        {
            // A name held by a process this token cannot open. The moves
            // below are each safe on their own; only the ordering guarantee
            // is lost, which the resume logic covers on the next start.
            return null;
        }
    }

    private static bool AcquireMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(LockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-run; the resume logic above copes
            // with whatever it left, so the lock is ours.
            return true;
        }
    }

    private static string BuildMutexName(string currentDirectory)
    {
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    currentDirectory.ToUpperInvariant())))[..20];
        return $"Local\\Dismode.StorageMigration.{fingerprint}";
    }
}

using Dismode.Data.Storage;

namespace Dismode.RecoveryTests;

/// <summary>
/// Recovery journals written under the old name have to follow the user to
/// the new directory; a journal left behind is a restore nobody runs.
/// </summary>
[TestClass]
public sealed class LegacyStorageMigrationTests
{
    [TestMethod]
    public void TheWholeLegacyDirectoryMovesWhenNothingExistsYet()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(Path.Combine(legacy, "OptiScaler"));
        File.WriteAllText(Path.Combine(legacy, "user-recovery.jsonl"), "journal");
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db"), "db");
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db-wal"), "wal");

        LegacyStorageMigrationResult result =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup);
        Assert.IsFalse(Directory.Exists(legacy));
        Assert.AreEqual(
            "journal",
            File.ReadAllText(Path.Combine(current, "user-recovery.jsonl")));
        Assert.AreEqual(
            "db",
            File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.AreEqual(
            "wal",
            File.ReadAllText(Path.Combine(current, "dismode-user.db-wal")));
        Assert.IsTrue(Directory.Exists(Path.Combine(current, "OptiScaler")));
    }

    [TestMethod]
    public void MergeMovesMissingEntriesAndNeverOverwritesAnything()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(legacy, "user-recovery.jsonl"), "journal");
        File.WriteAllText(Path.Combine(legacy, "ui-startup-errors.log"), "old");
        File.WriteAllText(Path.Combine(current, "ui-startup-errors.log"), "new");
        File.WriteAllText(Path.Combine(legacy, "same.txt"), "identical");
        File.WriteAllText(Path.Combine(current, "same.txt"), "identical");

        LegacyStorageMigrationResult result =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.HasCount(1, result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup);
        Assert.AreEqual(
            "journal",
            File.ReadAllText(Path.Combine(current, "user-recovery.jsonl")));
        Assert.AreEqual(
            "new",
            File.ReadAllText(Path.Combine(current, "ui-startup-errors.log")));
        Assert.AreEqual(
            "old",
            File.ReadAllText(
                Path.Combine(current, "ui-startup-errors.gameshift.log")),
            "Kolizja zostaje obok nowego pliku pod nazwa mowiaca skad pochodzi.");
        Assert.AreEqual(
            "identical",
            File.ReadAllText(Path.Combine(current, "same.txt")));
        Assert.IsFalse(
            File.Exists(Path.Combine(current, "same.gameshift.txt")),
            "Identyczna kopia nie jest dublowana.");
        Assert.IsFalse(
            Directory.Exists(legacy),
            "Po scaleniu stary katalog znika, wiec nastepny start nic nie zglasza.");
    }

    [TestMethod]
    public void DirectoriesPresentOnBothSidesAreMergedRecursively()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(Path.Combine(legacy, "Artwork", "Steam"));
        Directory.CreateDirectory(Path.Combine(current, "Artwork"));
        File.WriteAllText(Path.Combine(legacy, "Artwork", "a.png"), "a");
        File.WriteAllText(Path.Combine(legacy, "Artwork", "Steam", "b.png"), "b");
        File.WriteAllText(Path.Combine(current, "Artwork", "c.png"), "c");
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db"), "db");

        LegacyStorageMigrationResult result =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup);
        Assert.AreEqual("a", File.ReadAllText(Path.Combine(current, "Artwork", "a.png")));
        Assert.AreEqual("b", File.ReadAllText(Path.Combine(current, "Artwork", "Steam", "b.png")));
        Assert.AreEqual("c", File.ReadAllText(Path.Combine(current, "Artwork", "c.png")));
        Assert.AreEqual("db", File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.IsFalse(Directory.Exists(legacy));
    }

    [TestMethod]
    public void DatabaseRenameResumesAfterTheLegacyDirectoryIsGone()
    {
        // Przerwany przebieg: katalog juz przeniesiony, baza jeszcze pod stara
        // nazwa. Bez tego kolejny start zalozylby pusta dismode-user.db obok.
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "gameshift-user.db"), "db");
        File.WriteAllText(Path.Combine(current, "gameshift-user.db-shm"), "shm");

        LegacyStorageMigrationResult result =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup);
        Assert.AreEqual("db", File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.AreEqual("shm", File.ReadAllText(Path.Combine(current, "dismode-user.db-shm")));
        Assert.IsFalse(File.Exists(Path.Combine(current, "gameshift-user.db")));
    }

    [TestMethod]
    public void ALockedLegacyDatabaseBlocksStartupUntilItIsReleased()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(legacy);
        string database = Path.Combine(legacy, "gameshift-user.db");
        File.WriteAllText(database, "db");
        File.WriteAllText(Path.Combine(legacy, "user-recovery.jsonl"), "journal");

        LegacyStorageMigrationResult locked;
        using (new FileStream(
            database,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            locked = LegacyStorageMigration.Migrate(legacy, current);
        }

        Assert.IsTrue(
            locked.BlocksStartup,
            "Baza zostala pod stara nazwa, a nowej nie ma: start dalby pusty program.");
        Assert.IsNotEmpty(locked.Problems);
        Assert.IsFalse(File.Exists(Path.Combine(current, "dismode-user.db")));
        Assert.AreEqual(
            "journal",
            File.ReadAllText(Path.Combine(current, "user-recovery.jsonl")),
            "Odblokowane pliki przechodza mimo blokady na innym.");

        LegacyStorageMigrationResult released =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.IsEmpty(released.Problems, string.Join("; ", released.Problems));
        Assert.IsFalse(released.BlocksStartup);
        Assert.AreEqual("db", File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.IsFalse(Directory.Exists(legacy));
    }

    [TestMethod]
    public void ALockedIncidentalFileIsReportedWithoutBlockingStartup()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(legacy);
        string log = Path.Combine(legacy, "ui-startup-errors.log");
        File.WriteAllText(log, "log");
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db"), "db");

        LegacyStorageMigrationResult result;
        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = LegacyStorageMigration.Migrate(legacy, current);
        }

        Assert.HasCount(1, result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup);
        Assert.AreEqual("db", File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.IsTrue(File.Exists(log), "Zablokowany plik zostaje na miejscu.");
    }

    [TestMethod]
    public void ALegacyDatabaseMeetingANewOneIsKeptWholeUnderAnUnmergedName()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db"), "old db");
        File.WriteAllText(Path.Combine(legacy, "gameshift-user.db-wal"), "old wal");
        File.WriteAllText(Path.Combine(current, "dismode-user.db"), "new db");

        LegacyStorageMigrationResult result =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.HasCount(1, result.Problems, string.Join("; ", result.Problems));
        Assert.IsFalse(result.BlocksStartup, "Nowa baza istnieje, wiec start jej nie zgubi.");
        Assert.AreEqual("new db", File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.AreEqual(
            "old db",
            File.ReadAllText(Path.Combine(current, "gameshift-user.unmerged.db")));
        Assert.AreEqual(
            "old wal",
            File.ReadAllText(Path.Combine(current, "gameshift-user.unmerged.db-wal")));
        Assert.IsFalse(Directory.Exists(legacy));

        LegacyStorageMigrationResult again =
            LegacyStorageMigration.Migrate(legacy, current);
        Assert.IsEmpty(
            again.Problems,
            "Rozstrzygnieta kolizja nie jest zglaszana przy kazdym starcie.");
    }

    [TestMethod]
    public void NoLegacyDirectoryMeansNothingToDo()
    {
        using TemporaryDirectory root = new();
        string current = Path.Combine(root.Path, "Dismode");

        LegacyStorageMigrationResult result = LegacyStorageMigration.Migrate(
            Path.Combine(root.Path, "GameShift"),
            current);

        Assert.AreSame(LegacyStorageMigrationResult.Empty, result);
        Assert.IsFalse(Directory.Exists(current));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Dismode-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

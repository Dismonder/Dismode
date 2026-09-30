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

        IReadOnlyList<string> problems =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
        Assert.IsFalse(Directory.Exists(legacy));
        Assert.AreEqual(
            "journal",
            File.ReadAllText(Path.Combine(current, "user-recovery.jsonl")));
        Assert.AreEqual(
            "db",
            File.ReadAllText(Path.Combine(current, "dismode-user.db")));
        Assert.IsTrue(Directory.Exists(Path.Combine(current, "OptiScaler")));
    }

    [TestMethod]
    public void MissingEntriesAreMergedAndExistingOnesAreNeverOverwritten()
    {
        using TemporaryDirectory root = new();
        string legacy = Path.Combine(root.Path, "GameShift");
        string current = Path.Combine(root.Path, "Dismode");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(legacy, "user-recovery.jsonl"), "journal");
        File.WriteAllText(Path.Combine(legacy, "ui-startup-errors.log"), "old");
        File.WriteAllText(Path.Combine(current, "ui-startup-errors.log"), "new");

        IReadOnlyList<string> problems =
            LegacyStorageMigration.Migrate(legacy, current);

        Assert.AreEqual(1, problems.Count);
        Assert.AreEqual(
            "journal",
            File.ReadAllText(Path.Combine(current, "user-recovery.jsonl")));
        Assert.AreEqual(
            "new",
            File.ReadAllText(Path.Combine(current, "ui-startup-errors.log")));
        Assert.AreEqual(
            "old",
            File.ReadAllText(Path.Combine(legacy, "ui-startup-errors.log")),
            "Kolizja zostaje w starym katalogu zamiast zniknac.");
    }

    [TestMethod]
    public void NoLegacyDirectoryMeansNothingToDo()
    {
        using TemporaryDirectory root = new();
        string current = Path.Combine(root.Path, "Dismode");

        IReadOnlyList<string> problems = LegacyStorageMigration.Migrate(
            Path.Combine(root.Path, "GameShift"),
            current);

        Assert.AreEqual(0, problems.Count);
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

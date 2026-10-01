using System.Diagnostics;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Processes;

/// <summary>
/// The comparison that has to hold when a game library lives behind a
/// junction. These tests build a real junction in the temporary directory —
/// a directory junction needs no administrator and no developer mode, which
/// is exactly why moving a Steam library this way is so common.
/// </summary>
[TestClass]
public sealed class ExecutablePathIdentityTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"Dismode.PathIdentity.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void RemoveRoot() => DirectoryJunction.RemoveTree(_root);

    [TestMethod]
    public void IdenticalTextMatchesWithoutTouchingTheDisk()
    {
        string missing = Path.Combine(_root, "nie-istnieje", "gra.exe");

        Assert.IsTrue(ExecutablePathIdentity.AreSameExecutable(
            missing,
            missing.ToUpperInvariant()));
    }

    [TestMethod]
    public void UnreadablePathsNeverMatchEachOther()
    {
        // Dwie sciezki, ktorych nie da sie otworzyc, nie sa dowodem na nic.
        Assert.IsFalse(ExecutablePathIdentity.AreSameExecutable(
            Path.Combine(_root, "a", "gra.exe"),
            Path.Combine(_root, "b", "gra.exe")));
        Assert.IsFalse(ExecutablePathIdentity.AreSameExecutable(
            null,
            Path.Combine(_root, "gra.exe")));
        Assert.IsFalse(ExecutablePathIdentity.AreSameExecutable(
            Path.Combine(_root, "gra.exe"),
            null));
    }

    [TestMethod]
    public void TheSameFileReachedThroughRedundantSegmentsMatches()
    {
        string game = CreateFile("gra.exe");
        string detour = Path.Combine(_root, ".", "podkatalog", "..", "gra.exe");
        Directory.CreateDirectory(Path.Combine(_root, "podkatalog"));

        Assert.AreNotEqual(game, detour, StringComparer.OrdinalIgnoreCase);
        Assert.IsTrue(ExecutablePathIdentity.AreSameExecutable(game, detour));
    }

    [TestMethod]
    public void DifferentFilesInTheSameDirectoryDoNotMatch()
    {
        Assert.IsFalse(ExecutablePathIdentity.AreSameExecutable(
            CreateFile("gra.exe"),
            CreateFile("launcher.exe")));
    }

    [TestMethod]
    public void AGameBehindAJunctionMatchesTheRealPath()
    {
        string real = Path.Combine(_root, "dysk-d", "steamapps", "common", "Gra");
        Directory.CreateDirectory(real);
        string realGame = Path.Combine(real, "gra.exe");
        File.WriteAllText(realGame, "gra");

        string library = DirectoryJunction.Create(
            Path.Combine(_root, "biblioteka"),
            Path.Combine(_root, "dysk-d"));
        string libraryGame = Path.Combine(
            library,
            "steamapps",
            "common",
            "Gra",
            "gra.exe");

        Assert.IsTrue(
            File.Exists(libraryGame),
            "Junction musi prowadzic do tego samego pliku.");
        Assert.IsTrue(
            ExecutablePathIdentity.AreSameExecutable(realGame, libraryGame),
            "Sciezka z biblioteki i sciezka z jadra to ta sama gra.");
        Assert.IsTrue(
            ExecutablePathIdentity.AreSameExecutable(libraryGame, realGame),
            "Porownanie nie moze zalezec od kolejnosci argumentow.");
    }

    [TestMethod]
    public void ResolvingAJunctionGivesThePathBehindIt()
    {
        string real = Path.Combine(_root, "cel");
        Directory.CreateDirectory(real);
        string realGame = Path.Combine(real, "gra.exe");
        File.WriteAllText(realGame, "gra");
        string junction = DirectoryJunction.Create(
            Path.Combine(_root, "skrot"),
            real);

        string? resolved = ExecutablePathIdentity.TryResolveFinalPath(
            Path.Combine(junction, "gra.exe"));

        Assert.IsNotNull(resolved);
        Assert.AreEqual(
            realGame,
            resolved,
            StringComparer.OrdinalIgnoreCase,
            "Wynik ma byc zwykla sciezka, bez przedrostka \\\\?\\.");
    }

    [TestMethod]
    public void AMissingFileResolvesToNothingAndToItself()
    {
        string missing = Path.Combine(_root, "nie-ma.exe");

        Assert.IsNull(ExecutablePathIdentity.TryResolveFinalPath(missing));
        Assert.AreEqual(
            missing,
            ExecutablePathIdentity.ResolveFinalPathOrSelf(missing));
    }

    [TestMethod]
    public void ARunningProcessIsRecognisedThroughAJunctionOverItsDirectory()
    {
        // Zalozenie calej zmiany, sprawdzone na dzialajacym procesie, a nie
        // przyjete: Windows podaje sciezke JUZ rozwiazana, wiec profil
        // z junctionem nigdy nie zrowna sie z nia tekstem. Plik jest przy tym
        // otwarty na wylacznosc zapisu — uchwyt bez zadnego dostepu i tak go
        // nazywa, dokladnie jak dla dzialajacej gry.
        using Process current = Process.GetCurrentProcess();
        string? reported = ProcessImagePath.TryRead(current);
        Assert.IsNotNull(reported);

        string junction = DirectoryJunction.Create(
            Path.Combine(_root, "wydanie"),
            Path.GetDirectoryName(reported)!);
        string throughJunction = Path.Combine(
            junction,
            Path.GetFileName(reported));

        Assert.IsFalse(
            StringComparer.OrdinalIgnoreCase.Equals(reported, throughJunction),
            "Test ma sens tylko wtedy, gdy obie postacie roznia sie tekstem.");
        Assert.IsTrue(
            ExecutablePathIdentity.AreSameExecutable(reported, throughJunction),
            "To ten sam plik, mimo innej sciezki.");
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return path;
    }
}

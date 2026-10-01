using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Processes;

/// <summary>
/// What the classifier has to say about a game whose library was moved with
/// a junction. The profile keeps the path the launcher wrote down; Windows
/// reports the path behind the junction for the running process. Told apart,
/// the game itself and its helpers stop counting as ordinary background
/// applications — that is, Dismode offers to restrain the game it is
/// supposed to be protecting.
/// </summary>
[TestClass]
public sealed class ProcessClassificationGamePathTests
{
    private const int SessionId = 3;

    private string _root = string.Empty;
    private string _realGamePath = string.Empty;
    private string _libraryGamePath = string.Empty;

    [TestInitialize]
    public void CreateMovedLibrary()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"Dismode.Classification.{Guid.NewGuid():N}");
        string realDirectory = Path.Combine(
            _root,
            "dysk-d",
            "steamapps",
            "common",
            "Gra");
        Directory.CreateDirectory(realDirectory);
        _realGamePath = Path.Combine(realDirectory, "gra.exe");
        File.WriteAllText(_realGamePath, "gra");

        string library = DirectoryJunction.Create(
            Path.Combine(_root, "biblioteka"),
            Path.Combine(_root, "dysk-d"));
        _libraryGamePath = Path.Combine(
            library,
            "steamapps",
            "common",
            "Gra",
            "gra.exe");
    }

    [TestCleanup]
    public void RemoveLibrary() => DirectoryJunction.RemoveTree(_root);

    [TestMethod]
    public void TheGameItselfIsRecognisedThroughTheJunction()
    {
        ProcessClassification classification = Classify("gra", _realGamePath);

        Assert.AreEqual(
            ProcessSafetyClassification.GameInfrastructure,
            classification.Kind,
            "Proces gry nie moze trafic miedzy aplikacje tla.");
        StringAssert.Contains(classification.Reason, "Główny proces");
    }

    [TestMethod]
    public void AHelperFromTheInstallationDirectoryIsRecognisedThroughTheJunction()
    {
        string helper = Path.Combine(
            Path.GetDirectoryName(_realGamePath)!,
            "crashhandler.exe");

        ProcessClassification classification = Classify("crashhandler", helper);

        Assert.AreEqual(
            ProcessSafetyClassification.GameInfrastructure,
            classification.Kind,
            "Skladnik pomocniczy gry ma zostac pozostawiony.");
        StringAssert.Contains(classification.Reason, "katalogu instalacyjnego");
    }

    [TestMethod]
    public void AHelperBesideTheExecutableDirectoryIsRecognisedThroughAGameDirectoryJunction()
    {
        // Junction na samym katalogu gry, nie na bibliotece: za nim nie ma
        // juz znacznika steamapps\common, wiec root instalacji musi byc
        // rozwiazany z biblioteki, a nie wyprowadzony z rozwiazanej sciezki
        // EXE, ktora wskazalaby tylko podkatalog bin.
        string realGameDirectory = Path.Combine(_root, "dysk-e", "Gra");
        Directory.CreateDirectory(Path.Combine(realGameDirectory, "bin"));
        Directory.CreateDirectory(Path.Combine(realGameDirectory, "helpers"));
        File.WriteAllText(Path.Combine(realGameDirectory, "bin", "gra.exe"), "gra");
        string helper = Path.Combine(realGameDirectory, "helpers", "crashhandler.exe");
        File.WriteAllText(helper, "helper");
        string libraryGameDirectory = DirectoryJunction.Create(
            Path.Combine(_root, "dysk-d", "steamapps", "common", "Gra2"),
            realGameDirectory);
        string libraryGamePath = Path.Combine(libraryGameDirectory, "bin", "gra.exe");

        ProcessClassification classification =
            ProcessClassificationService.Classify(
                "crashhandler",
                helper,
                SessionId,
                SessionId,
                libraryGamePath);

        Assert.AreEqual(
            ProcessSafetyClassification.GameInfrastructure,
            classification.Kind,
            "Helper obok katalogu z EXE nalezy do gry przeniesionej junctionem.");
        StringAssert.Contains(classification.Reason, "katalogu instalacyjnego");
    }

    [TestMethod]
    public void AProcessOutsideTheGameStaysAnOrdinaryBackgroundApplication()
    {
        string unrelated = Path.Combine(_root, "obce", "przegladarka.exe");

        ProcessClassification classification =
            Classify("przegladarka", unrelated);

        Assert.AreEqual(
            ProcessSafetyClassification.OptionalUser,
            classification.Kind,
            "Rozwiazywanie junctionow nie moze rozszerzac ochrony na obce "
                + "procesy.");
    }

    [TestMethod]
    public void TheSamePathOnBothSidesStillMatchesWithoutAJunction()
    {
        ProcessClassification classification =
            ProcessClassificationService.Classify(
                "gra",
                _realGamePath,
                SessionId,
                SessionId,
                _realGamePath);

        Assert.AreEqual(
            ProcessSafetyClassification.GameInfrastructure,
            classification.Kind);
        StringAssert.Contains(classification.Reason, "Główny proces");
    }

    /// <summary>
    /// The process path as Windows reports it — past the junction — against
    /// the profile path as the launcher wrote it down.
    /// </summary>
    private ProcessClassification Classify(
        string processName,
        string processExecutablePath) =>
        ProcessClassificationService.Classify(
            processName,
            processExecutablePath,
            SessionId,
            SessionId,
            _libraryGamePath);
}

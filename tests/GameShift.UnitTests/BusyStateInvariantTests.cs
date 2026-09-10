using System.Text.RegularExpressions;

namespace GameShift.UnitTests;

/// <summary>
/// The busy flag gates seventeen handlers with <c>if (_isBusy) return;</c>.
/// If it is ever left set, every one of them starts refusing silently and the
/// window looks alive while doing nothing — the worst failure mode a program
/// has, because there is nothing to report and nothing to retry.
/// <para>
/// Two rules keep that from happening, and both are easy to break by accident:
/// the flag is written in exactly one place, and every raise is lowered in a
/// <c>finally</c>. The OptiScaler path had broken the first one — it set the
/// field directly, so the progress indicator never appeared and the buttons
/// stayed enabled while silently ignoring clicks through an uninstall that
/// verifies the checksum of every installed file.
/// </para>
/// </summary>
[TestClass]
public sealed class BusyStateInvariantTests
{
    [TestMethod]
    public void TheBusyFlagHasExactlyOneWriter()
    {
        string source = WithoutComments(ReadMainWindowSource());
        MatchCollection assignments = Regex.Matches(
            source,
            @"_isBusy\s*=\s*[^=]");

        Assert.AreEqual(
            1,
            assignments.Count,
            "Flaga zajętości ma być ustawiana wyłącznie w SetBusy. Zapis "
                + "w innym miejscu pomija wskaźnik postępu i wyłączanie "
                + "przycisków, więc okno wygląda na aktywne, a klika się "
                + "w próżnię.");

        int writer = assignments[0].Index;
        int setBusy = source.IndexOf(
            "private void SetBusy(",
            StringComparison.Ordinal);
        Assert.IsGreaterThan(
            0,
            setBusy,
            "Nie znaleziono metody SetBusy.");
        Assert.IsGreaterThan(
            setBusy,
            writer,
            "Jedyny zapis flagi leży poza SetBusy.");
    }

    [TestMethod]
    public void EveryBusyRaiseIsLoweredInAFinallyBlock()
    {
        string source = WithoutComments(ReadMainWindowSource());
        int raised = Regex.Count(source, @"SetBusy\(true\)");
        MatchCollection lowered = Regex.Matches(source, @"SetBusy\(false\)");

        Assert.AreEqual(
            raised,
            lowered.Count,
            "Liczba podniesień i opuszczeń flagi zajętości się nie zgadza.");
        Assert.IsGreaterThan(
            5,
            raised,
            "Znaleziono podejrzanie mało wywołań — test przestał cokolwiek "
                + "sprawdzać.");

        List<int> outsideFinally = [];
        foreach (Match match in lowered)
        {
            // Wystarczy spojrzec wstecz o kilkaset znakow: finally domykajace
            // operacje lezy tuz nad opuszczeniem flagi.
            int from = Math.Max(0, match.Index - 400);
            if (!source[from..match.Index].Contains(
                "finally",
                StringComparison.Ordinal))
            {
                outsideFinally.Add(LineOf(source, match.Index));
            }
        }

        Assert.AreEqual(
            0,
            outsideFinally.Count,
            "Flaga zajętości jest opuszczana poza blokiem finally w liniach: "
                + string.Join(", ", outsideFinally)
                + ". Wyjątek w tej operacji zostawi program zablokowany "
                + "do czasu ponownego uruchomienia.");
    }

    private static int LineOf(string source, int index) =>
        source[..index].Count(character => character == '\n') + 1;

    private static string WithoutComments(string source) =>
        Regex.Replace(source, "//[^\r\n]*", string.Empty);

    private static string ReadMainWindowSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Nie znaleziono katalogu repozytorium.");
        return File.ReadAllText(Path.Combine(
            directory.FullName,
            "src",
            "GameShift.UI",
            "MainWindow.xaml.cs"));
    }
}

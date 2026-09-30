using System.Text.RegularExpressions;

namespace Dismode.UnitTests;

/// <summary>
/// Guards the one path where an unhandled exception costs the whole product:
/// startup.
/// <para>
/// The window's Loaded handler is <c>async void</c>, because that is what an
/// event handler has to be. In WinUI an exception escaping such a method does
/// not fail the operation — it ends the process. On the startup path that
/// means the application never appears and says nothing about why, which is
/// indistinguishable from a machine problem and impossible for a user to act
/// on.
/// </para>
/// <para>
/// Three methods awaited there had no handling at all when this was written:
/// the profile store read, and the two component status reads that talk to a
/// separate process and a Windows service. All three fail for ordinary
/// reasons — a busy database, a service that is not installed. One of them
/// also built a dictionary with <c>ToDictionary</c>, so a duplicated profile
/// id in the database would have locked the user out of their own program
/// permanently.
/// </para>
/// </summary>
[TestClass]
public sealed class StartupResilienceTests
{
    private const string StartupHandler = "OnRootLoaded";

    [TestMethod]
    public void EverythingAwaitedDuringStartupHandlesItsOwnFailures()
    {
        string source = ReadMainWindowSource();
        IReadOnlyList<string> awaited = AwaitedMethodsIn(
            ExtractMethod(source, StartupHandler));

        Assert.IsGreaterThan(
            3,
            awaited.Count,
            "Nie znaleziono wywołań w procedurze startowej — test przestał "
                + "cokolwiek sprawdzać.");

        List<string> unguarded = [];
        foreach (string name in awaited)
        {
            string? body = TryExtractMethod(source, name);
            if (body is null)
            {
                // Metoda spoza tego pliku — nie mamy jej tu do sprawdzenia.
                continue;
            }

            if (!body.Contains("catch", StringComparison.Ordinal))
            {
                unguarded.Add(name);
            }
        }

        Assert.AreEqual(
            0,
            unguarded.Count,
            "Te metody są oczekiwane w " + StartupHandler + " i mogą rzucić "
                + "wyjątkiem, który zakończy proces zamiast pokazać okno:\n"
                + string.Join("\n", unguarded));
    }

    /// <summary>
    /// ToDictionary throws on a duplicate key. Anywhere else that is a bug
    /// report; on the startup path it is a program that stops opening until
    /// somebody edits the database by hand.
    /// </summary>
    [TestMethod]
    public void ProfileMetadataIsNotIndexedWithAThrowingCall()
    {
        string body = WithoutComments(
            ExtractMethod(ReadMainWindowSource(), "LoadProfilesAsync"));

        Assert.IsFalse(
            body.Contains(".ToDictionary(", StringComparison.Ordinal),
            "LoadProfilesAsync buduje słownik przez ToDictionary. "
                + "Zduplikowany identyfikator profilu w bazie rzuci wtedy "
                + "ArgumentException na ścieżce startowej.");
    }

    /// <summary>
    /// Strips line comments before matching. The first version of this test
    /// failed on the comment explaining why ToDictionary is not used — looking
    /// for a name in prose rather than in code.
    /// </summary>
    private static string WithoutComments(string body) =>
        Regex.Replace(body, "//[^\r\n]*", string.Empty);

    private static IReadOnlyList<string> AwaitedMethodsIn(string body) =>
        [.. Regex
            .Matches(body, @"await\s+(?:\w+\.)?(\w+Async)\s*\(")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)];

    private static string ExtractMethod(string source, string name) =>
        TryExtractMethod(source, name)
        ?? throw new InvalidOperationException(
            $"Nie znaleziono metody {name} w MainWindow.xaml.cs.");

    /// <summary>
    /// Returns the method body by brace counting. Crude, but it reads the file
    /// the compiler reads rather than a copy of the rules kept in the test.
    /// </summary>
    private static string? TryExtractMethod(string source, string name)
    {
        Match declaration = Regex.Match(
            source,
            @"(?:private|public|internal|protected)[^\r\n(){}]*\s"
                + Regex.Escape(name)
                + @"\s*\(");
        if (!declaration.Success)
        {
            return null;
        }

        int start = declaration.Index;
        int open = source.IndexOf('{', declaration.Index);
        int arrow = source.IndexOf("=>", declaration.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            // Cialo wyrazeniowe: konczy sie na sredniku.
            int semicolon = source.IndexOf(';', arrow);
            return semicolon < 0 ? null : source[start..semicolon];
        }

        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[start..(index + 1)];
                }
            }
        }

        return null;
    }

    private static string ReadMainWindowSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Nie znaleziono katalogu repozytorium.");
        return File.ReadAllText(Path.Combine(
            directory.FullName,
            "src",
            "Dismode.UI",
            "MainWindow.xaml.cs"));
    }
}

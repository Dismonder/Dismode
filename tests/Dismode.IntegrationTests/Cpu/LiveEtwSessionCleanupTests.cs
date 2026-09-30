using System.Diagnostics;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Cpu;

/// <summary>
/// Sesja pomiaru klatek nie ma prawa przezyc pomiaru.
/// <para>
/// Sesja zdarzen w Windows zyje niezaleznie od procesu, ktory ja zalozyl.
/// Gdy PresentMon zniknie bez porzadnego zamkniecia, sesja zostaje i od tego
/// momentu zdarzenia klatek wpadaja do niej, a nie do kolejnych pomiarow.
/// Psuje to nie tylko Dismode, ale KAZDE narzedzie mierzace klatki na tej
/// maszynie, i nie widac tego jako bledu — pomiar po prostu zwraca zero
/// klatek.
/// </para>
/// <para>
/// Zmierzone w trakcie prac nad tym modulem: cztery kolejne pomiary
/// zwrocily zero klatek, dopoki osierocona sesja nie zostala zatrzymana
/// recznie. Przyczyna byla podwojna — zatrzymanie siedzialo tylko w galezi
/// "proces jeszcze zyje", a sprzatanie przy starcie znalo wylacznie nazwy
/// z dawnych wydan, nie biezaca. Ten test pilnuje samego mechanizmu
/// zatrzymywania, bo bez niego oba te bledy byly niewidoczne.
/// </para>
/// </summary>
[TestClass]
public sealed class LiveEtwSessionCleanupTests
{
    private const string SessionName = "Dismode-test-sprzatania";
    private string _presentMonPath = string.Empty;

    [TestInitialize]
    public void Prepare()
    {
        _presentMonPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Dismode",
            "Tools",
            "PresentMon",
            "PresentMon-2.5.1-x64.exe");
        if (!File.Exists(_presentMonPath))
        {
            _presentMonPath = Path.Combine(
                AppContext.BaseDirectory,
                "Tools",
                "PresentMon",
                "PresentMon-2.5.1-x64.exe");
        }
    }

    [TestCleanup]
    public void Cleanup() =>
        // Nawet gdy test padnie, nie zostawiamy po sobie tego, czego pilnuje.
        EtwSessionCleanup.StopStaleSessions([SessionName]);

    [TestMethod]
    [TestCategory("Live")]
    [Timeout(120_000)]
    public void OrphanedSessionIsStoppedAndCaptureWorksAgain()
    {
        if (!File.Exists(_presentMonPath))
        {
            Assert.Inconclusive(
                $"Brak PresentMon pod {_presentMonPath}; bez niego nie da sie "
                    + "zalozyc prawdziwej sesji zdarzen.");
            return;
        }

        EtwSessionCleanup.StopStaleSessions([SessionName]);
        Assert.IsFalse(
            SessionIsRunning(),
            "Sesja testowa istniala przed testem; przerwany wczesniejszy "
                + "przebieg nie zostal posprzatany.");

        LeaveOrphanedSession();
        Assert.IsTrue(
            SessionIsRunning(),
            "Nie udalo sie zalozyc osieroconej sesji, wiec test nie sprawdza "
                + "tego, co mial sprawdzac. Zabicie PresentMon powinno "
                + "zostawic sesje dzialajaca.");

        IReadOnlyList<string> stopped =
            EtwSessionCleanup.StopStaleSessions([SessionName]);

        CollectionAssert.Contains(
            stopped.ToArray(),
            SessionName,
            "Sprzatanie nie zgloosilo zatrzymania sesji, ktora dzialala.");
        Assert.IsFalse(
            SessionIsRunning(),
            "Sesja nadal dziala po sprzataniu. Dopoki tak jest, kazdy pomiar "
                + "klatek na tej maszynie zwraca zero i nie widac dlaczego.");
    }

    [TestMethod]
    public void StoppingAMissingSessionIsNotAnError()
    {
        // Brak sesji to normalny stan, nie awaria — sprzatanie wola sie przed
        // kazdym pomiarem i w przewazajacej wiekszosci nie ma czego zdejmowac.
        IReadOnlyList<string> stopped = EtwSessionCleanup.StopStaleSessions(
            ["Dismode-takiej-sesji-nie-ma"]);

        Assert.IsEmpty(stopped);
    }

    /// <summary>
    /// Zaklada sesje i zabija PresentMon bez zamkniecia, zeby sesja zostala.
    /// </summary>
    private void LeaveOrphanedSession()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _presentMonPath,
            WorkingDirectory = Path.GetDirectoryName(_presentMonPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            "--output_stdout",
            "--no_console_stats",
            "--v2_metrics",
            "--session_name",
            SessionName,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process presentMon = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic PresentMon.");
        try
        {
            Thread.Sleep(4000);
        }
        finally
        {
            if (!presentMon.HasExited)
            {
                presentMon.Kill(entireProcessTree: true);
                presentMon.WaitForExit(5000);
            }
        }

        Thread.Sleep(1000);
    }

    private static bool SessionIsRunning()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "logman.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("query");
        startInfo.ArgumentList.Add("-ets");

        using Process logman = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Nie udalo sie uruchomic logman.");
        string output = logman.StandardOutput.ReadToEnd();
        logman.WaitForExit(10_000);
        return output.Contains(SessionName, StringComparison.OrdinalIgnoreCase);
    }
}

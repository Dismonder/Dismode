using System.Diagnostics;
using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests.Processes;

[TestClass]
public sealed class ProcessMemoryTrimmerTests
{
    [TestMethod]
    public async Task TryTrimWorkingSetSucceedsOnRunningProcess()
    {
        await using ProcessHarnessFixture fixture =
            await ProcessHarnessFixture.StartAsync();

        bool trimmed = ProcessMemoryTrimmer.TryTrimWorkingSet(
            fixture.Process,
            out long freedBytes);

        Assert.IsTrue(trimmed);
        Assert.IsTrue(freedBytes >= 0);
    }

    [TestMethod]
    public void ProtectedShellProcessesAreNeverTrimmed()
    {
        // Opróżnienie zbioru roboczego powloki widac natychmiast: menu Start
        // i wyszukiwanie przestaja reagowac. Trimmer ma je pomijac niezaleznie
        // od tego, ktora sciezka go wywolala.
        string[] neverTrim =
        [
            "TextInputHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "explorer",
            "ctfmon",
            "dwm",
            "RuntimeBroker",
        ];

        // Wlasne narzedzie pomiarowe Dismodea. Zagłodzone gubi zdarzenia
        // ETW, a zgubione zdarzenia odczytuja sie jako dlugie klatki — program
        // raportowalby przyciecie, ktore sam wywolal.
        Assert.IsTrue(
            BackgroundApplicationGuard.IsProtectedProcessName(
                "PresentMon-2.5.1-x64"),
            "PresentMon musi byc chroniony niezaleznie od wersji w nazwie.");

        foreach (string name in neverTrim)
        {
            Assert.IsTrue(
                BackgroundApplicationGuard.IsProtectedProcessName(name),
                $"{name} musi byc chroniony przed trimowaniem.");
        }

        foreach (Process process in neverTrim
            .SelectMany(Process.GetProcessesByName)
            .Take(4))
        {
            using (process)
            {
                Assert.IsFalse(
                    ProcessMemoryTrimmer.TryTrimWorkingSet(
                        process,
                        out long freed),
                    $"{process.ProcessName} zostal ztrimowany mimo ochrony.");
                Assert.AreEqual(0, freed);
            }
        }
    }

    [TestMethod]
    public void TryTrimWorkingSetReturnsFalseOnInvalidProcessId()
    {
        bool trimmed = ProcessMemoryTrimmer.TryTrimWorkingSet(
            -99999,
            out long freedBytes);

        Assert.IsFalse(trimmed);
        Assert.AreEqual(0, freedBytes);
    }
}

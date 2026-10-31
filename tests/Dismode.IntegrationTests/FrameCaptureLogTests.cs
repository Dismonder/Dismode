using Dismode.Windows.Processes;

namespace Dismode.IntegrationTests;

[TestClass]
public sealed class FrameCaptureLogTests
{
    /// <summary>
    /// Each provider test writes the frame-capture log. Without this the
    /// suite would append to the user's real
    /// <c>%LOCALAPPDATA%\Dismode\frame-capture.log</c>.
    /// </summary>
    [AssemblyInitialize]
    public static void RedirectLogForTheWholeSuite(TestContext context)
    {
        _ = context;
        FrameCaptureLog.PathOverride = Path.Combine(
            Path.GetTempPath(),
            "Dismode.IntegrationTests",
            $"frame-capture-{Environment.ProcessId}.log");
    }

    [TestMethod]
    public void LineIsKeptWhileAnotherProcessHoldsTheFileOpen()
    {
        // Nakladka (UI) i sesja (SessionHost) pisza do tego samego pliku.
        // File.AppendAllText odmawial dostepu, gdy drugi proces trzymal plik,
        // a wyjatek byl polykany — linia znikala bez sladu.
        string? previous = FrameCaptureLog.PathOverride;
        string path = Path.Combine(
            Path.GetTempPath(),
            $"dismode-framelog-{Guid.NewGuid():N}.log");
        FrameCaptureLog.PathOverride = path;
        try
        {
            using FileStream otherProcess = new(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);

            FrameCaptureLog.Write("pierwsza");
            FrameCaptureLog.Write("druga");

            using FileStream reader = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string text = new StreamReader(reader).ReadToEnd();
            StringAssert.Contains(text, "pierwsza");
            StringAssert.Contains(text, "druga");
        }
        finally
        {
            FrameCaptureLog.PathOverride = previous;
            File.Delete(path);
        }
    }

    [TestMethod]
    public void UnusableLocationNeverThrows()
    {
        // Dziennik nigdy nie moze kosztowac pomiaru — takze przy sciezce,
        // ktorej system w ogole nie przyjmie.
        string? previous = FrameCaptureLog.PathOverride;
        FrameCaptureLog.PathOverride = "C:\\bad\0path\\frame.log";
        try
        {
            FrameCaptureLog.Write("nic");
        }
        finally
        {
            FrameCaptureLog.PathOverride = previous;
        }
    }
}

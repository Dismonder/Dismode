using System.Diagnostics;

namespace GameShift.SecurityTests;

/// <summary>
/// Pins the property the update handoff depends on: a file held open with
/// <see cref="FileShare.Read"/> can still be executed, but cannot be replaced.
/// <para>
/// The updater verifies the downloaded installer's SHA-256 and then starts it
/// with <c>Verb = "runas"</c>. Closing the file between those two steps leaves
/// a window in which anything running as the user can swap the bytes — and the
/// user then approves an elevation prompt for what they believe is GameShift's
/// own update. That is a local privilege escalation, and narrowing the window
/// does not fix it; the file has to stay locked across the handoff.
/// </para>
/// <para>
/// Whether Windows will execute an image that is open with a write-denying
/// share mode is not something to reason about — it decides whether the fix
/// works or breaks updates entirely. Measured on this machine: it executes,
/// and the overwrite fails. This test keeps that true.
/// </para>
/// </summary>
[TestClass]
public sealed class InstallerHandoffTests
{
    [TestMethod]
    public void AHeldExecutableStillRunsButCannotBeReplaced()
    {
        string source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "where.exe");
        if (!File.Exists(source))
        {
            Assert.Inconclusive(
                "Brak where.exe — test potrzebuje dowolnego programu "
                    + "konsolowego z systemu.");
            return;
        }

        string copy = Path.Combine(
            Path.GetTempPath(),
            $"GameShift-handoff-{Guid.NewGuid():N}.exe");
        File.Copy(source, copy);

        try
        {
            using FileStream guard = new(
                copy,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            Assert.ThrowsExactly<IOException>(
                () =>
                {
                    using FileStream _ = new(
                        copy,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.None);
                },
                "Plik trzymany przez weryfikację dał się otworzyć do zapisu. "
                    + "Ktoś mógłby podmienić instalator między sprawdzeniem "
                    + "sumy kontrolnej a jego uruchomieniem z podniesionymi "
                    + "uprawnieniami.");

            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = copy,
                Arguments = "where.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            Assert.IsNotNull(
                started,
                "Windows nie uruchomił pliku trzymanego z blokadą zapisu. "
                    + "Gdyby tak było, trzymanie uchwytu przez uruchomienie "
                    + "zepsułoby aktualizacje zamiast je zabezpieczyć.");
            started.WaitForExit(10_000);
        }
        finally
        {
            TryDelete(copy);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

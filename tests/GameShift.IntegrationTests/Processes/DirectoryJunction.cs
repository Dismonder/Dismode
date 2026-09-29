using System.Diagnostics;

namespace GameShift.IntegrationTests.Processes;

/// <summary>
/// Builds the thing under test: a directory junction, the one reparse point
/// Windows creates without administrator rights and without developer mode.
/// That is why moving a game library with it is so common, and why GameShift
/// has to compare paths by the file they reach rather than by their text.
/// </summary>
internal static class DirectoryJunction
{
    /// <summary>
    /// Creates <paramref name="link"/> pointing at <paramref name="target"/>
    /// and returns the link. Fails the test when Windows refuses, rather than
    /// letting a test pass without ever exercising a junction.
    /// </summary>
    public static string Create(string link, string target)
    {
        // mklink jest poleceniem wbudowanym cmd; .NET nie wystawia tworzenia
        // junctiona, a Directory.CreateSymbolicLink robi cos innego i zwykle
        // wymaga trybu dewelopera.
        using Process? process = Process.Start(new ProcessStartInfo(
            "cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.IsNotNull(process);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"mklink /J nie utworzyl junctiona: {error}");
        return link;
    }

    /// <summary>
    /// Removes a tree that may contain junctions. A recursive delete follows
    /// a junction into its target, so every link is unhooked as a directory
    /// of its own first.
    /// </summary>
    public static void RemoveTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            Stack<string> pending = new();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (string directory in Directory.GetDirectories(
                    pending.Pop()))
                {
                    if (File.ResolveLinkTarget(
                            directory,
                            returnFinalTarget: false)
                        is not null)
                    {
                        // Odpinamy dowiazanie, zamiast wchodzic do srodka.
                        Directory.Delete(directory);
                        continue;
                    }

                    pending.Push(directory);
                }
            }

            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

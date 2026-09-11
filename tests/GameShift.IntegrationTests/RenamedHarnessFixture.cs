using System.Diagnostics;
using GameShift.Windows.Processes;

namespace GameShift.IntegrationTests;

/// <summary>
/// A copy of the process test harness running under a different name from a
/// directory of its own, so it classifies as an ordinary optional user
/// process rather than as a helper from the game's installation directory.
/// That is what lets a session test approve it as a background application
/// and act on it — the harness playing the game and the one playing the
/// background have to be different executables in different places.
/// </summary>
internal sealed class RenamedHarnessFixture : IAsyncDisposable
{
    private readonly string _readyFile;

    private RenamedHarnessFixture(
        Process process,
        string executablePath,
        string readyFile)
    {
        Process = process;
        ExecutablePath = executablePath;
        _readyFile = readyFile;
    }

    internal Process Process { get; }

    internal string ExecutablePath { get; }

    internal static async ValueTask<RenamedHarnessFixture> StartAsync(
        string testDirectory)
    {
        string sourceExecutable =
            ProcessHarnessFixture.FindHarnessExecutable();
        string sourceDirectory =
            Path.GetDirectoryName(sourceExecutable)
            ?? throw new InvalidOperationException(
                "The harness directory is unavailable.");
        string targetDirectory =
            Path.Combine(testDirectory, "background");
        Directory.CreateDirectory(targetDirectory);
        foreach (string sourceFile
                     in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(
                sourceFile,
                Path.Combine(
                    targetDirectory,
                    Path.GetFileName(sourceFile)));
        }

        string executablePath =
            Path.Combine(targetDirectory, "BackgroundWorker.exe");
        File.Copy(sourceExecutable, executablePath);
        string readyFile =
            Path.Combine(testDirectory, "background.ready");
        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFile);
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The renamed background harness did not start.");

        try
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(10));
            while (!File.Exists(readyFile))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        "The renamed background harness exited with code "
                        + $"{process.ExitCode}.");
                }

                await Task.Delay(
                    TimeSpan.FromMilliseconds(50),
                    timeout.Token);
            }

            process.Refresh();
            return new(process, executablePath, readyFile);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!Process.HasExited)
            {
                _ = ProcessWindowHelper.RequestGracefulClose(Process);
                using CancellationTokenSource timeout =
                    new(TimeSpan.FromSeconds(3));
                try
                {
                    await Process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Process.Kill(entireProcessTree: true);
                    await Process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            Process.Dispose();
            if (File.Exists(_readyFile))
            {
                File.Delete(_readyFile);
            }
        }
    }
}

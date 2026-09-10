using System.ComponentModel;
using System.Diagnostics;
using GameShift.Data.Journal;
using GameShift.Data.Storage;
using GameShift.Windows.Security;
using GameShift.Windows.Sessions;

namespace GameShift.SessionHost;

internal static class SessionHostUpdatePreparation
{
    private static readonly TimeSpan GracefulCloseTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TerminationTimeout =
        TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(
        bool stopUserInterface,
        bool restoreSystemOptimizer,
        CancellationToken cancellationToken)
    {
        try
        {
            // Instalacje OptiScalera nie sa w journalu — maja wlasne manifesty,
            // bo dotycza katalogow gier, a nie ustawien systemu. Odinstalowanie
            // GameShifta zostawiloby te pliki w kazdej grze i zabralo jedyne
            // narzedzie, ktore potrafi je stamtad usunac.
            if (restoreSystemOptimizer
                && OptiScalerInstallationSurvey.Describe()
                    is string outstanding)
            {
                Console.Error.WriteLine(outstanding);
                return 8;
            }

            RecoveryJournalInspection before =
                await RecoveryJournalInspector.InspectAsync(
                    GameShiftStoragePaths.UserRecoveryJournalPath,
                    cancellationToken);
            if (!before.IsClean)
            {
                WriteUnfinishedSessions(before);
                return 3;
            }

            string applicationDirectory =
                Path.GetFullPath(AppContext.BaseDirectory);
            if (stopUserInterface &&
                !await PrepareSystemOptimizerForMaintenanceAsync(
                    restoreSystemOptimizer,
                    cancellationToken))
            {
                return restoreSystemOptimizer ? 7 : 6;
            }

            bool userInterfaceWasRunning = stopUserInterface &&
                await StopExactComponentAsync(
                    Path.Combine(applicationDirectory, "GameShift.UI.exe"),
                    preferGracefulClose: true,
                    cancellationToken);

            await Task.Delay(
                TimeSpan.FromMilliseconds(300),
                cancellationToken);
            RecoveryJournalInspection afterUiClose =
                await RecoveryJournalInspector.InspectAsync(
                    GameShiftStoragePaths.UserRecoveryJournalPath,
                    cancellationToken);
            if (!afterUiClose.IsClean)
            {
                WriteUnfinishedSessions(afterUiClose);
                if (userInterfaceWasRunning)
                {
                    TryRestartUserInterface(applicationDirectory);
                }

                return 3;
            }

            await StopExactComponentAsync(
                Path.Combine(
                    applicationDirectory,
                    "Tools",
                    "PresentMon",
                    "PresentMon-2.5.1-x64.exe"),
                preferGracefulClose: false,
                cancellationToken);
            if (stopUserInterface)
            {
                await StopExactComponentAsync(
                    Path.Combine(
                        applicationDirectory,
                        "SystemOptimizer",
                        "GameShift.SystemOptimizer.exe"),
                    preferGracefulClose: true,
                    cancellationToken);
            }

            await StopExactComponentAsync(
                Path.Combine(
                    applicationDirectory,
                    "GameShift.SessionHost.exe"),
                preferGracefulClose: false,
                cancellationToken);

            RecoveryJournalInspection final =
                await RecoveryJournalInspector.InspectAsync(
                    GameShiftStoragePaths.UserRecoveryJournalPath,
                    cancellationToken);
            if (!final.IsClean)
            {
                WriteUnfinishedSessions(final);
                if (userInterfaceWasRunning)
                {
                    TryRestartUserInterface(applicationDirectory);
                }

                return 3;
            }

            Console.WriteLine(
                "GameShift components stopped; recovery journal is clean.");
            return 0;
        }
        catch (InvalidDataException exception)
        {
            Console.Error.WriteLine(
                "Recovery journal integrity check failed: "
                + exception.Message);
            return 4;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or InvalidOperationException
                or TimeoutException)
        {
            Console.Error.WriteLine(
                "GameShift components were not stopped safely: "
                + exception.Message);
            return 5;
        }
    }

    private static async Task<bool> PrepareSystemOptimizerForMaintenanceAsync(
        bool restoreAll,
        CancellationToken cancellationToken)
    {
        bool hasMachineState =
            File.Exists(GameShiftStoragePaths.SystemOptimizerDatabasePath)
            || File.Exists(GameShiftStoragePaths.MachineRecoveryJournalPath);
        if (!hasMachineState)
        {
            return true;
        }

        // Demanding a restore that has nothing to undo would be a dead end on
        // builds whose signature the agent does not trust: restore is a
        // mutation, the policy refuses mutations from untrusted callers, and
        // the uninstaller would block forever. If the machine journal records
        // no applied-but-uncompensated action, no durable change exists.
        RecoveryJournalInspection machineJournal =
            await RecoveryJournalInspector.InspectAsync(
                GameShiftStoragePaths.MachineRecoveryJournalPath,
                cancellationToken);
        if (machineJournal.IsClean && !machineJournal.HasPendingDurableChanges)
        {
            return true;
        }

        using SystemOptimizerGameProfileClient client = new(
            CurrentWindowsIdentity.GetUserSid().Value);
        SystemGameProfileOperationResult result = restoreAll
            ? await client.RestoreAllAsync(cancellationToken)
                .ConfigureAwait(false)
            : await client.PrepareForUpdateAsync(cancellationToken)
                .ConfigureAwait(false);
        if (restoreAll && result.Succeeded)
        {
            result = await client.PrepareForUpdateAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            Console.Error.WriteLine(result.Message);
        }

        return result.Succeeded;
    }

    private static async Task<bool> StopExactComponentAsync(
        string expectedExecutablePath,
        bool preferGracefulClose,
        CancellationToken cancellationToken)
    {
        string expectedPath = Path.GetFullPath(expectedExecutablePath);
        string processName = Path.GetFileNameWithoutExtension(expectedPath);
        bool stoppedAny = false;
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.Id == Environment.ProcessId
                    || !IsExactProcess(process, expectedPath))
                {
                    continue;
                }

                stoppedAny = true;
                if (preferGracefulClose && process.CloseMainWindow())
                {
                    using CancellationTokenSource gracefulTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                    gracefulTimeout.CancelAfter(GracefulCloseTimeout);
                    try
                    {
                        await process.WaitForExitAsync(
                            gracefulTimeout.Token);
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    using CancellationTokenSource terminationTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken);
                    terminationTimeout.CancelAfter(TerminationTimeout);
                    try
                    {
                        await process.WaitForExitAsync(
                            terminationTimeout.Token);
                    }
                    catch (OperationCanceledException exception)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"Proces {processName} nie zakończył się.",
                            exception);
                    }
                }
            }
        }

        return stoppedAny;
    }

    private static bool IsExactProcess(
        Process process,
        string expectedExecutablePath)
    {
        try
        {
            string? actualPath = process.MainModule?.FileName;
            return actualPath is not null
                && StringComparer.OrdinalIgnoreCase.Equals(
                    Path.GetFullPath(actualPath),
                    expectedExecutablePath);
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryRestartUserInterface(string applicationDirectory)
    {
        string launcherPath = Path.Combine(
            applicationDirectory,
            "GameShift.exe");
        if (!File.Exists(launcherPath))
        {
            return;
        }

        try
        {
            using Process? _ = Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = applicationDirectory,
                UseShellExecute = false,
            });
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or InvalidOperationException
                or IOException)
        {
        }
    }

    private static void WriteUnfinishedSessions(
        RecoveryJournalInspection inspection) =>
        Console.Error.WriteLine(
            "Recovery journal contains unfinished session(s): "
            + string.Join(
                ", ",
                inspection.UnfinishedSessionIds.Select(
                    sessionId => sessionId.ToString("D"))));
}

using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace GameShift.MemoryOptimizer.ComponentControl;

internal sealed record MemoryOptimizerComponentDisableResult(
    bool Succeeded,
    string Message);

internal static class MemoryOptimizerComponentShutdown
{
    private const string ServiceName = "GameShiftMemoryService";
    private const string TaskName = "GameShift Memory Optimizer";
    private const int TaskNotFound = 1;
    private const int ServiceNotFound = 1060;
    private const int ServiceNotActive = 1062;
    private const int UserCancelledElevation = 1223;

    internal static async Task<MemoryOptimizerComponentDisableResult> DisableAsync(
        CancellationToken cancellationToken)
    {
        string executablePath = ResolveCurrentExecutable();
        using Process helper = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = "--disable-component",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            },
        };

        try
        {
            if (!helper.Start())
            {
                return new(
                    false,
                    "Windows nie uruchomił podwyższonego wyłączania komponentu.");
            }

            await helper.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode == UserCancelledElevation)
        {
            return new(false, "Anulowano potwierdzenie UAC. Komponent nadal działa.");
        }

        return helper.ExitCode == 0
            ? new(
                true,
                "Memory Optimizer został wyłączony. Włączenie wymaga opcji Napraw.")
            : new(
                false,
                DescribeFailure(helper.ExitCode));
    }

    internal static int RunElevatedDisable()
    {
        try
        {
            int taskResult = RunFixedCommand(
                "schtasks.exe",
                "/Change",
                "/TN",
                TaskName,
                "/DISABLE");
            if (taskResult != 0 && taskResult != TaskNotFound)
            {
                return 10;
            }

            int serviceResult = RunFixedCommand(
                "sc.exe",
                "config",
                ServiceName,
                "start=",
                "disabled");
            if (serviceResult != 0 && serviceResult != ServiceNotFound)
            {
                return 20;
            }

            if (!StopService())
            {
                return 30;
            }

            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 40;
        }
        catch (Win32Exception)
        {
            return 40;
        }
        catch (InvalidOperationException)
        {
            return 40;
        }
    }

    private static string ResolveCurrentExecutable()
    {
        string executablePath = Path.GetFullPath(
            Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "Nie można ustalić ścieżki Memory Optimizer."));
        string installRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!executablePath.StartsWith(
                installRoot,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                "Ścieżka procesu Memory Optimizer nie jest zaufana.");
        }

        return executablePath;
    }

    private static int RunFixedCommand(
        string fileName,
        params string[] arguments)
    {
        string executablePath = Path.Combine(
            Environment.SystemDirectory,
            fileName);
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Windows nie uruchomił {fileName}.");
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool StopService()
    {
        using ServiceController service = new(ServiceName);
        try
        {
            service.Refresh();
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode == ServiceNotFound)
        {
            return true;
        }

        if (service.Status is ServiceControllerStatus.Stopped)
        {
            return true;
        }

        try
        {
            if (service.Status is not ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(
                ServiceControllerStatus.Stopped,
                TimeSpan.FromSeconds(30));
            return service.Status == ServiceControllerStatus.Stopped;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception exception) when (
            exception.NativeErrorCode is ServiceNotFound or ServiceNotActive)
        {
            return true;
        }
    }

    private static string DescribeFailure(int exitCode) =>
        exitCode switch
        {
            10 => "Nie udało się wyłączyć zadania startowego Memory Optimizer.",
            20 => "Nie udało się wyłączyć automatycznego startu usługi Memory Optimizer.",
            30 => "Nie udało się zatrzymać usługi Memory Optimizer.",
            40 => "Windows odmówił uprawnień do wyłączenia Memory Optimizer.",
            _ =>
                $"Wyłączanie Memory Optimizer zakończyło się kodem {exitCode}.",
        };
}

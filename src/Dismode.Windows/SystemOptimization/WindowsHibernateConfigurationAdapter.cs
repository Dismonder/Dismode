using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Dismode.Windows.SystemOptimization;

public sealed class WindowsHibernateConfigurationAdapter :
    IHibernateConfigurationAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public ValueTask<HibernateConfigurationState> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using RegistryKey? power = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power",
                writable: false);
            object? raw = power?.GetValue("HibernateEnabled");
            return raw is null
                ? ValueTask.FromResult(
                    new HibernateConfigurationState(
                        Enabled: false,
                        IsSupported: false))
                : ValueTask.FromResult(
                    new HibernateConfigurationState(
                        Convert.ToInt32(raw, CultureInfo.InvariantCulture) != 0,
                        IsSupported: true));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidCastException
                or FormatException
                or OverflowException)
        {
            return ValueTask.FromResult(
                new HibernateConfigurationState(
                    Enabled: false,
                    IsSupported: false));
        }
    }

    public async ValueTask SetAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        string executablePath = Path.GetFullPath(
            Path.Combine(Environment.SystemDirectory, "powercfg.exe"));
        string expectedDirectory = Path.GetFullPath(Environment.SystemDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(executablePath)
            || !StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetDirectoryName(executablePath)?.TrimEnd(
                    Path.DirectorySeparatorChar),
                expectedDirectory))
        {
            throw new FileNotFoundException(
                "The trusted System32 powercfg executable is unavailable.",
                executablePath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = expectedDirectory,
        };
        startInfo.ArgumentList.Add("/hibernate");
        startInfo.ArgumentList.Add(enabled ? "on" : "off");

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Windows did not start the fixed hibernation command.");
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminateOwnedProcess(process);
            throw new TimeoutException(
                "The fixed hibernation command exceeded its 30-second timeout.");
        }
        catch (OperationCanceledException)
        {
            TryTerminateOwnedProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            string error = await process.StandardError.ReadToEndAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                $"powercfg returned exit code {process.ExitCode}: "
                + error.Trim()[..Math.Min(error.Trim().Length, 512)]);
        }
    }

    private static void TryTerminateOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or NotSupportedException)
        {
        }
    }
}

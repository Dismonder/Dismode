using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace GameShift.UI.Services;

public sealed class MemoryOptimizerComponentService
{
    private const string ServiceName = "GameShiftMemoryService";
    private const int MaximumStatusBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _userSid;
    private readonly string _componentRoot;
    private readonly string _trayExecutablePath;
    private readonly string _statusPath;

    public MemoryOptimizerComponentService(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        _userSid = userSid.Trim();
        _componentRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "MemoryOptimizer"));
        _trayExecutablePath = Path.GetFullPath(
            Path.Combine(
                _componentRoot,
                "Tray",
                "GameShift.MemoryOptimizer.exe"));
        _statusPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "GameShift",
            "MemoryOptimizer",
            _userSid,
            "component-status-v1.json");
    }

    public async Task<MemoryOptimizerComponentSnapshot> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_trayExecutablePath))
        {
            return new(
                MemoryOptimizerComponentState.NotInstalled,
                "Niezainstalowany",
                "Opcjonalny składnik Memory Optimizer nie jest zainstalowany.",
                false);
        }

        ServiceControllerStatus? serviceStatus = TryGetServiceStatus();
        ComponentStatusDocument? status = await TryReadStatusAsync(
            cancellationToken);
        bool paused = status?.SchemaVersion == 1 && status.IsPaused;
        return MemoryOptimizerComponentPresentation.CreateInstalledSnapshot(
            serviceStatus is ServiceControllerStatus.Running,
            IsTrayRunning(),
            paused);
    }

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_trayExecutablePath))
        {
            throw new FileNotFoundException(
                "Memory Optimizer nie jest zainstalowany.",
                _trayExecutablePath);
        }

        string? parent = Path.GetDirectoryName(_trayExecutablePath);
        if (parent is null ||
            !Path.GetFullPath(parent).StartsWith(
                _componentRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ścieżka Memory Optimizer opuściła katalog komponentu.");
        }

        bool interfaceWasRunning = IsTrayRunning();
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = _trayExecutablePath,
            WorkingDirectory = parent,
            UseShellExecute = false,
        });
        if (process is null)
        {
            throw new InvalidOperationException(
                "Windows nie uruchomił Memory Optimizer.");
        }

        Task exitTask = process.WaitForExitAsync(cancellationToken);
        Task observationDelay = Task.Delay(
            TimeSpan.FromSeconds(2),
            cancellationToken);
        Task completed = await Task.WhenAny(exitTask, observationDelay);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != exitTask)
        {
            return;
        }

        await exitTask;
        string? failure = MemoryOptimizerComponentPresentation.GetLaunchFailure(
            interfaceWasRunning,
            launchedProcessExited: true,
            process.ExitCode);
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private bool IsTrayRunning()
    {
        string processName = Path.GetFileNameWithoutExtension(
            _trayExecutablePath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? executablePath = process.MainModule?.FileName;
                    if (!process.HasExited && executablePath is not null &&
                        string.Equals(
                            Path.GetFullPath(executablePath),
                            _trayExecutablePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        NotSupportedException or Win32Exception)
                {
                    // A process that cannot be verified is not treated as this component.
                }
            }
        }

        return false;
    }

    private static ServiceControllerStatus? TryGetServiceStatus()
    {
        try
        {
            using ServiceController service = new(ServiceName);
            return service.Status;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private async Task<ComponentStatusDocument?> TryReadStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            FileInfo statusFile = new(_statusPath);
            if (!statusFile.Exists || statusFile.Length <= 0 ||
                statusFile.Length > MaximumStatusBytes)
            {
                return null;
            }

            await using FileStream stream = new(
                _statusPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<ComponentStatusDocument>(
                stream,
                JsonOptions,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException)
        {
            return null;
        }
    }

    private sealed record ComponentStatusDocument(
        int SchemaVersion,
        bool IsPaused,
        bool IsOptimizationRunning,
        DateTimeOffset UpdatedAtUtc,
        string ServiceVersion);
}

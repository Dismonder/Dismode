using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dismode.MemoryOptimizer.Core.Ipc;
using Dismode.MemoryOptimizer.Core.Models;

namespace Dismode.MemoryOptimizer.Core.Activity;

public interface IGameActivityGuard
{
    ValueTask<GuardDecision> EvaluateAsync(
        CancellationToken cancellationToken);
}

public sealed class AllowAllGameActivityGuard : IGameActivityGuard
{
    public ValueTask<GuardDecision> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GuardDecision.Allowed);
    }
}

public sealed record RunningProcessInfo(
    int ProcessId,
    string Name,
    bool IsForeground,
    bool IsFullScreen);

public static class NeutralGameStatePaths
{
    public static string GetUserDirectory(string userSid)
    {
        _ = MemoryOptimizerProtocol.CreatePipeName(userSid);
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "Dismode",
            "Shared",
            userSid);
    }

    public static string GetKnownGamesPath(string userSid) =>
        Path.Combine(GetUserDirectory(userSid), "known-games-v1.json");

    public static string GetActiveGamePath(string userSid) =>
        Path.Combine(GetUserDirectory(userSid), "active-game-v1.json");
}

public static class GameActivityEvaluator
{
    private static readonly HashSet<string> ProtectedProcesses = new(
        [
            "EasyAntiCheat",
            "EasyAntiCheat_EOS",
            "BEService",
            "BEService_x64",
            "vgc",
            "vgtray",
            "RiotClientServices",
            "EpicGamesLauncher",
            "steam",
            "steamwebhelper",
            "GalaxyClient",
            "Battle.net",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> FullScreenAllowList = new(
        [
            "explorer",
            "ShellExperienceHost",
            "SearchHost",
            "StartMenuExperienceHost",
            "Dismode.UI",
            "Dismode.MemoryOptimizer",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static GuardDecision Evaluate(
        ActiveGameDocument? activeGame,
        KnownGamesDocument? knownGames,
        IReadOnlyList<RunningProcessInfo> processes,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(processes);

        if (activeGame is { SchemaVersion: 1 } &&
            activeGame.ExpiresAtUtc >= nowUtc &&
            processes.Any(process =>
                process.ProcessId == activeGame.ProcessId))
        {
            return new(
                true,
                "dismode-active-session",
                $"Optimization blocked while {activeGame.DisplayName} is active.");
        }

        HashSet<string> knownExecutables = new(
            StringComparer.OrdinalIgnoreCase);
        if (knownGames is { SchemaVersion: 1 })
        {
            foreach (KnownGameEntry game in knownGames.Games)
            {
                foreach (string executable in game.ExecutableNames)
                {
                    knownExecutables.Add(Path.GetFileNameWithoutExtension(
                        executable));
                }
            }
        }

        foreach (RunningProcessInfo process in processes)
        {
            string name = Path.GetFileNameWithoutExtension(process.Name);
            if (knownExecutables.Contains(name))
            {
                return new(
                    true,
                    "known-game-process",
                    $"Optimization blocked by known game process {name}.");
            }

            if (ProtectedProcesses.Contains(name))
            {
                return new(
                    true,
                    "protected-game-process",
                    $"Optimization blocked by protected launcher or anti-cheat {name}.");
            }

            if (process.IsForeground && process.IsFullScreen &&
                !FullScreenAllowList.Contains(name))
            {
                return new(
                    true,
                    "unknown-fullscreen-process",
                    $"Optimization blocked by unknown full-screen process {name}.");
            }
        }

        return GuardDecision.Allowed;
    }
}

public sealed partial class WindowsGameActivityGuard : IGameActivityGuard
{
    private readonly string _knownGamesPath;
    private readonly string _activeGamePath;
    public WindowsGameActivityGuard(string userSid)
    {
        _knownGamesPath = NeutralGameStatePaths.GetKnownGamesPath(userSid);
        _activeGamePath = NeutralGameStatePaths.GetActiveGamePath(userSid);
    }

    public ValueTask<GuardDecision> EvaluateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ActiveGameDocument? activeGame = TryRead<ActiveGameDocument>(
            _activeGamePath);
        KnownGamesDocument? knownGames = TryRead<KnownGamesDocument>(
            _knownGamesPath);
        IReadOnlyList<RunningProcessInfo> processes = CaptureProcesses(
            cancellationToken);
        return ValueTask.FromResult(GameActivityEvaluator.Evaluate(
            activeGame,
            knownGames,
            processes,
            DateTimeOffset.UtcNow));
    }

    private static T? TryRead<T>(string path)
    {
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length <= 0 ||
                file.Length > MemoryOptimizerProtocol.MaximumMessageBytes)
            {
                return default;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(
                stream,
                MemoryOptimizerProtocol.JsonOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException)
        {
            return default;
        }
    }

    private static List<RunningProcessInfo> CaptureProcesses(
        CancellationToken cancellationToken)
    {
        nint foregroundWindow = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foregroundWindow, out uint foregroundPid);
        bool foregroundFullScreen = IsFullScreen(foregroundWindow);
        List<RunningProcessInfo> results = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    results.Add(new(
                        process.Id,
                        process.ProcessName,
                        process.Id == foregroundPid,
                        process.Id == foregroundPid && foregroundFullScreen));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                    results.Add(new(process.Id, "unknown", false, false));
                }
            }
        }

        return results;
    }

    private static bool IsFullScreen(nint window)
    {
        if (window == nint.Zero || !GetWindowRect(window, out Rect windowRect))
        {
            return false;
        }

        nint monitor = MonitorFromWindow(window, 2);
        MonitorInfo info = new()
        {
            Size = checked((uint)Marshal.SizeOf<MonitorInfo>()),
        };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        return Math.Abs(windowRect.Left - info.Monitor.Left) <= tolerance &&
            Math.Abs(windowRect.Top - info.Monitor.Top) <= tolerance &&
            Math.Abs(windowRect.Right - info.Monitor.Right) <= tolerance &&
            Math.Abs(windowRect.Bottom - info.Monitor.Bottom) <= tolerance;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(
        nint window,
        out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "GetMonitorInfoW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }
}

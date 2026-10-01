using System.ComponentModel;
using System.Diagnostics;

namespace Dismode.Windows.Processes;

/// <summary>
/// Finds components of releases published under the old name (GameShift)
/// that still run in this user's session. Their mutexes, pipes and process
/// names carry the old name, so neither the single-instance locks nor the
/// host lookup see them, yet they optimize the same games and, until the
/// first Dismode start moves it, own the same data directory. Two products
/// working the same session would fight over priorities and journals, and a
/// migration under a running GameShift would split its data in half.
/// </summary>
public static class LegacyProductProcesses
{
    /// <summary>Process names (without extension) of the interactive legacy components.</summary>
    public static IReadOnlyList<string> ProcessNames =>
    [
        "GameShift.UI",
        "GameShift.SessionHost",
    ];

    /// <summary>
    /// Executable path of the first legacy UI or host running in the current
    /// Windows session, or null when none is. A process whose path cannot be
    /// read still counts: the name alone is proof enough that it exists.
    /// </summary>
    public static string? FindRunningInCurrentSession()
    {
        int currentSessionId;
        using (Process current = Process.GetCurrentProcess())
        {
            currentSessionId = current.SessionId;
        }

        foreach (string processName in ProcessNames)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != currentSessionId)
                        {
                            continue;
                        }

                        return ProcessImagePath.TryRead(process)
                            ?? processName + ".exe";
                    }
                    catch (Exception exception) when (
                        exception is
                            InvalidOperationException
                            or Win32Exception
                            or NotSupportedException)
                    {
                        // The process ended between enumeration and the query.
                    }
                }
            }
        }

        return null;
    }
}

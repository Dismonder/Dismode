using System.ComponentModel;
using System.Diagnostics;

namespace Dismode.Windows.Processes;

public sealed class ProcessInventory : IProcessInventory
{
    public IReadOnlyList<ProcessSnapshot> Capture()
    {
        List<ProcessSnapshot> snapshots = [];

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                ProcessSnapshot? snapshot = TryCapture(process);
                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }
        }

        return snapshots
            .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.ProcessId)
            .ToArray();
    }

    private static ProcessSnapshot? TryCapture(Process process)
    {
        try
        {
            return new(
                ProcessId: process.Id,
                Name: process.ProcessName,
                StartedAtUtc: TryReadStartTime(process),
                ExecutablePath: TryReadExecutablePath(process),
                SessionId: process.SessionId,
                WorkingSetBytes: TryRead(() => process.WorkingSet64, fallback: 0L),
                TotalProcessorTime: TryRead(
                    () => process.TotalProcessorTime,
                    fallback: TimeSpan.Zero),
                HasMainWindow: TryRead(
                    () => process.MainWindowHandle != IntPtr.Zero,
                    fallback: false),
                PriorityClass: TryRead(
                    () => process.PriorityClass.ToString(),
                    fallback: "Unknown"));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryReadExecutablePath(Process process)
    {
        try
        {
            return ProcessImagePath.TryRead(process);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return null;
        }
    }

    private static T TryRead<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            return fallback;
        }
    }
}

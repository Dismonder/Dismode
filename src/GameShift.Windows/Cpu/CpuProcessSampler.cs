using System.ComponentModel;
using System.Diagnostics;

namespace GameShift.Windows.Cpu;

/// <summary>
/// One process, reduced to what the restraint loop actually reasons about.
/// </summary>
public sealed record CpuProcessSample(
    int ProcessId,
    string Name,
    DateTimeOffset StartedAtUtc,
    TimeSpan TotalProcessorTime);

public interface ICpuProcessSource
{
    IReadOnlyList<CpuProcessSample> Capture();
}

/// <summary>
/// Enumerates running processes for the restraint loop, reading only the four
/// values it uses.
/// <para>
/// The general-purpose inventory reads considerably more per process — the
/// executable path, whether there is a main window, the priority class — and
/// each of those costs a handle or a window enumeration. That is the right
/// trade for a diagnostics screen the user opens now and then. It is the wrong
/// one for a loop that runs every couple of seconds for the whole session,
/// because the cost is paid while a game is on screen and arrives as a burst
/// rather than a trickle.
/// </para>
/// <para>
/// A process without a readable start time is skipped: its id alone is not a
/// stable identity, since Windows hands ids out again after a process ends.
/// </para>
/// </summary>
public sealed class CpuProcessSampler : ICpuProcessSource
{
    public IReadOnlyList<CpuProcessSample> Capture()
    {
        List<CpuProcessSample> samples = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    samples.Add(new(
                        process.Id,
                        process.ProcessName,
                        new DateTimeOffset(
                            process.StartTime.ToUniversalTime(),
                            TimeSpan.Zero),
                        process.TotalProcessorTime));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException
                        or Win32Exception
                        or NotSupportedException)
                {
                    // Proces zakonczyl sie w trakcie wyliczania albo nalezy do
                    // innego kontekstu bezpieczenstwa. Jedno i drugie znaczy
                    // tylko tyle, ze nie jest kandydatem.
                }
            }
        }

        return samples;
    }
}

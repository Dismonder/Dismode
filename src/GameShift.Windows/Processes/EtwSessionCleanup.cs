using System.Runtime.InteropServices;

namespace GameShift.Windows.Processes;

/// <summary>
/// Stops event tracing sessions GameShift left behind.
/// <para>
/// An ETW session outlives the process that created it. Kill PresentMon hard —
/// a crash, Task Manager, a machine that went to sleep mid-capture — and the
/// session stays running with nobody consuming it. From then on the frame
/// events go into that orphan and every later capture, GameShift's own and
/// every other tool's, sees nothing but "events were lost".
/// </para>
/// <para>
/// Measured on the development machine: a session named
/// <c>gameshift-frametime</c>, created by a GameShift old enough to predate
/// per-user session names, had been running long enough that frame measurement
/// was silently broken for every application on that computer. Stopping it
/// brought capture back on the first try — 606 frames where the previous
/// attempt got zero. <c>--stop_existing_session</c> never helped, because it
/// only stops a session with the name the current build happens to use.
/// </para>
/// </summary>
internal static class EtwSessionCleanup
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorWmiInstanceNotFound = 4201;
    private const uint EventTraceControlStop = 1;

    /// <summary>
    /// Session names GameShift has used across versions. A build only stops a
    /// session it could itself have created; anything else on the machine
    /// belongs to another tool and is none of our business.
    /// </summary>
    internal static IReadOnlyList<string> LegacySessionNames =>
    [
        "gameshift-frametime",
        "GameShiftFrameCapture",
    ];

    /// <summary>
    /// Stops the named sessions, ignoring the ones that are not running.
    /// Returns the names actually stopped, so the caller can say what it did
    /// rather than claim it silently.
    /// </summary>
    internal static IReadOnlyList<string> StopStaleSessions(
        IEnumerable<string> sessionNames)
    {
        ArgumentNullException.ThrowIfNull(sessionNames);
        List<string> stopped = [];
        foreach (string name in sessionNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && TryStop(name))
            {
                stopped.Add(name);
            }
        }

        return stopped;
    }

    /// <summary>
    /// True when the session was running and is not any more. A session owned
    /// by another account needs privileges we deliberately do not ask for, so
    /// that failure is reported as "not stopped" rather than thrown.
    /// </summary>
    private static bool TryStop(string sessionName)
    {
        // EVENT_TRACE_PROPERTIES is a fixed header followed by room for the
        // logger and log file names. The names go in the trailing space and
        // the header points at them by offset.
        int headerSize = Marshal.SizeOf<EventTraceProperties>();
        int bufferSize = headerSize + (2 * MaximumNameBytes);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int index = 0; index < bufferSize; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            EventTraceProperties properties = new()
            {
                Wnode = new WnodeHeader
                {
                    BufferSize = (uint)bufferSize,
                },
                LoggerNameOffset = (uint)headerSize,
                LogFileNameOffset = (uint)(headerSize + MaximumNameBytes),
            };
            Marshal.StructureToPtr(properties, buffer, false);

            uint result = ControlTrace(
                0,
                sessionName,
                buffer,
                EventTraceControlStop);
            return result == ErrorSuccess;
        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException or DllNotFoundException)
        {
            // Nie ma po czym sprzatac na systemie bez tego API.
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int MaximumNameBytes = 1024;

    [DllImport(
        "advapi32.dll",
        EntryPoint = "ControlTraceW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint ControlTrace(
        ulong traceHandle,
        string instanceName,
        IntPtr properties,
        uint controlCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }
}

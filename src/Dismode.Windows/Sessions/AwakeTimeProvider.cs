using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dismode.Windows.Sessions;

internal sealed partial class AwakeTimeProvider : TimeProvider
{
    internal static readonly AwakeTimeProvider Instance = new();
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        if (!QueryUnbiasedInterruptTime(out ulong time))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return checked((long)time);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}

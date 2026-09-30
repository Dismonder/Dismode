using System.Diagnostics;
using Dismode.Windows.NativeInterop;

namespace Dismode.Windows.Processes;

public static class ProcessMemoryTrimmer
{
    public static bool TryTrimWorkingSet(int processId, out long freedBytes)
    {
        freedBytes = 0;
        try
        {
            using Process process = Process.GetProcessById(processId);
            return TryTrimWorkingSet(process, out freedBytes);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryTrimWorkingSet(Process process, out long freedBytes)
    {
        freedBytes = 0;
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (process.HasExited)
            {
                return false;
            }

            // Powloka, stos wejscia i kompozytor sa poza zasiegiem. Opróżnienie
            // ich zbioru roboczego widac natychmiast: menu Start i wyszukiwanie
            // przestaja reagowac, pasek zadan przerysowuje sie z opoznieniem,
            // a odzyskana pamiec wraca do nich po sekundzie i tak. Lista jest
            // ta sama, ktora chroni procesy przed usypianiem — jedno zrodlo
            // prawdy zamiast drugiej kopii, ktora sie rozjedzie.
            if (BackgroundApplicationGuard.IsProtectedProcessName(
                    process.ProcessName))
            {
                return false;
            }

            process.Refresh();
            long before = process.WorkingSet64;

            bool success = ProcessNativeMethods.K32EmptyWorkingSet(process.SafeHandle);
            if (!success)
            {
                success = ProcessNativeMethods.SetProcessWorkingSetSize(
                    process.SafeHandle,
                    nuint.MaxValue,
                    nuint.MaxValue);
            }

            if (success)
            {
                process.Refresh();
                long after = process.WorkingSet64;
                freedBytes = Math.Max(0, before - after);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

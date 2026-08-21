using System.Diagnostics;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Processes;

public static class SystemMemoryCleaner
{
    public static bool TryFlushSystemFileCache()
    {
        try
        {
            return ProcessNativeMethods.SetSystemFileCacheSize(
                -1,
                -1,
                0);
        }
        catch
        {
            return false;
        }
    }

    public static SystemMemoryPurgeResult PurgeMemory(IEnumerable<int>? candidateProcessIds = null)
    {
        long totalFreedBytes = 0;
        int trimmedProcesses = 0;

        if (candidateProcessIds is not null)
        {
            foreach (int processId in candidateProcessIds)
            {
                if (processId <= 0 || processId == Environment.ProcessId)
                {
                    continue;
                }

                if (ProcessMemoryTrimmer.TryTrimWorkingSet(processId, out long freed))
                {
                    totalFreedBytes += freed;
                    trimmedProcesses++;
                }
            }
        }

        bool cacheFlushed = TryFlushSystemFileCache();

        return new SystemMemoryPurgeResult(
            totalFreedBytes,
            trimmedProcesses,
            cacheFlushed);
    }
}

public sealed record SystemMemoryPurgeResult(
    long TotalWorkingSetFreedBytes,
    int ProcessesTrimmedCount,
    bool SystemCacheFlushed);

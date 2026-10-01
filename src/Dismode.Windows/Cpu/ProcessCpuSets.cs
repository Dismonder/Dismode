using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dismode.Windows.Cpu;

/// <summary>
/// Default CPU sets for a process — Windows' soft counterpart to an affinity
/// mask.
/// <para>
/// An affinity mask is a rule: the process may never run anywhere else, so a
/// mask that turns out to be too small starves it. Default CPU sets are a
/// preference: the scheduler keeps the process on those processors while it
/// can, and spills over when it must. For steering a game onto the fast cores
/// or into one cache group that is the better tool, because being wrong about
/// the size costs nothing.
/// </para>
/// <para>
/// One trap makes this worth doing carefully. A process that already has a
/// restricted affinity mask ignores default CPU sets entirely — Windows will
/// not schedule outside the mask no matter what sets are installed — yet
/// reading the sets back still returns what was written. Verifying by
/// read-back alone would report success for an assignment that cannot take
/// effect, so the caller has to notice the mask first.
/// </para>
/// </summary>
internal static partial class ProcessCpuSets
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetLimitedInformation = 0x2000;
    private const int MaximumCpuSetIds = 1024;

    /// <summary>
    /// True when a pre-existing affinity mask would silently defeat CPU sets:
    /// the wanted processors are not all inside what the process is already
    /// allowed to use.
    /// </summary>
    internal static bool WouldBeDefeatedByAffinity(
        ulong currentAffinityMask,
        ulong desiredMask) =>
        currentAffinityMask != 0
        && desiredMask != 0
        && (desiredMask & ~currentAffinityMask) != 0;

    internal static IReadOnlyList<uint>? TryRead(int processId)
    {
        using SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            checked((uint)processId));
        if (handle.IsInvalid)
        {
            return null;
        }

        // Wywolanie sondujace rozmiar zwraca false, gdy zbiory sa ustawione:
        // to zwykly wzorzec Win32 "bufor za maly", a nie blad. Liczy sie
        // wylacznie wypelniona liczba wymaganych identyfikatorow.
        _ = GetProcessDefaultCpuSets(handle, null, 0, out uint required);

        if (required == 0)
        {
            // Zadne domyslne zbiory nie sa ustawione — proces korzysta
            // z calej maszyny. To stan wyjsciowy, nie blad.
            return [];
        }

        if (required > MaximumCpuSetIds)
        {
            return null;
        }

        uint[] ids = new uint[required];
        return GetProcessDefaultCpuSets(handle, ids, required, out uint written)
            && written <= required
            ? ids[..checked((int)written)]
            : null;
    }

    /// <summary>
    /// Installs the given set ids, or clears the assignment when the list is
    /// empty so the process goes back to using the whole machine.
    /// </summary>
    internal static bool TryApply(int processId, IReadOnlyList<uint> cpuSetIds)
    {
        ArgumentNullException.ThrowIfNull(cpuSetIds);
        if (cpuSetIds.Count > MaximumCpuSetIds)
        {
            return false;
        }

        try
        {
            using SafeProcessHandle handle = OpenProcess(
                ProcessQueryLimitedInformation | ProcessSetLimitedInformation,
                false,
                checked((uint)processId));
            if (handle.IsInvalid)
            {
                return false;
            }

            return cpuSetIds.Count == 0
                ? SetProcessDefaultCpuSets(handle, null, 0)
                : SetProcessDefaultCpuSets(
                    handle,
                    [.. cpuSetIds],
                    checked((uint)cpuSetIds.Count));
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDefaultCpuSets(
        SafeProcessHandle process,
        uint[]? cpuSetIds,
        uint cpuSetIdCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessDefaultCpuSets(
        SafeProcessHandle process,
        uint[]? cpuSetIds,
        uint cpuSetIdCount,
        out uint requiredIdCount);
}

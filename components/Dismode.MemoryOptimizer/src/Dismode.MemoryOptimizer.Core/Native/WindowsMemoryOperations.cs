// SPDX-License-Identifier: GPL-3.0-only
// Derived from Windows Memory Cleaner 3.0.8 © Igor Mundstein.
// Upstream: https://github.com/IgorMundstein/WinMemoryCleaner/tree/3.0.8
// Modified for Windows 11 23H2+, .NET 10/x64, cancellation and per-area results.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Dismode.MemoryOptimizer.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace Dismode.MemoryOptimizer.Core.Native;

public interface IMemoryMetricsSource
{
    MemorySnapshot Capture();
}

public interface IMemoryAreaOperations
{
    MemoryAreaResult Execute(
        MemoryArea area,
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken);
}

public sealed class WindowsMemoryMetricsSource : IMemoryMetricsSource
{
    public MemorySnapshot Capture()
    {
        MemoryStatusEx status = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return WindowsMemoryMetricsMapper.Map(DateTimeOffset.UtcNow, status);
    }
}

internal static class WindowsMemoryMetricsMapper
{
    internal static MemorySnapshot Map(
        DateTimeOffset capturedAtUtc,
        MemoryStatusEx status) =>
        new(
            capturedAtUtc,
            status.TotalPhysical,
            status.AvailablePhysical,
            status.TotalVirtual,
            status.AvailableVirtual,
            status.MemoryLoad,
            status.TotalPageFile,
            status.AvailablePageFile);
}

public sealed class WindowsMemoryAreaOperations : IMemoryAreaOperations
{
    private readonly SecurityIdentifier _userSid;

    public WindowsMemoryAreaOperations(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        _userSid = new SecurityIdentifier(userSid);
        if (!_userSid.IsAccountSid())
        {
            throw new ArgumentException(
                "A Windows account SID is required.",
                nameof(userSid));
        }
    }

    public MemoryAreaResult Execute(
        MemoryArea area,
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            (string message, ulong released) = area switch
            {
                MemoryArea.CombinedPageList =>
                    (CombinePhysicalPages(), 0UL),
                MemoryArea.ModifiedFileCache =>
                    (FlushModifiedFileCache(cancellationToken), 0UL),
                MemoryArea.ModifiedPageList =>
                    (ExecuteMemoryListCommand(
                        NativeMethods.MemoryFlushModifiedList), 0UL),
                MemoryArea.RegistryCache => (ReconcileRegistryCache(), 0UL),
                MemoryArea.StandbyList =>
                    (ExecuteMemoryListCommand(
                        NativeMethods.MemoryPurgeStandbyList), 0UL),
                MemoryArea.StandbyListLowPriority =>
                    (ExecuteMemoryListCommand(
                        NativeMethods.MemoryPurgeLowPriorityStandbyList), 0UL),
                MemoryArea.SystemFileCache => (FlushSystemFileCache(), 0UL),
                MemoryArea.WorkingSet => TrimWorkingSets(
                    settings,
                    cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(area),
                    area,
                    "A single known memory area is required."),
            };
            stopwatch.Stop();
            return new(
                area,
                true,
                stopwatch.Elapsed,
                null,
                message,
                released);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception exception)
        {
            stopwatch.Stop();
            return new(
                area,
                false,
                stopwatch.Elapsed,
                exception.NativeErrorCode,
                exception.Message);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UnauthorizedAccessException)
        {
            stopwatch.Stop();
            return new(
                area,
                false,
                stopwatch.Elapsed,
                null,
                exception.Message);
        }
    }

    private static string CombinePhysicalPages()
    {
        using PrivilegeScope _ = PrivilegeScope.Enable(
            PrivilegeScope.ProfileSingleProcessPrivilege);
        MemoryCombineInformationEx information = new();
        int status = NativeMethods.NtSetSystemInformationCombine(
            NativeMethods.SystemCombinePhysicalMemoryInformation,
            ref information,
            checked((uint)Marshal.SizeOf<MemoryCombineInformationEx>()));
        ThrowForNtStatus(status);
        return "Combined identical physical pages.";
    }

    private static string ReconcileRegistryCache()
    {
        int status = NativeMethods.NtSetSystemInformationEmpty(
            NativeMethods.SystemRegistryReconciliationInformation,
            nint.Zero,
            0);
        ThrowForNtStatus(status);
        return "Reconciled registry cache.";
    }

    private static string ExecuteMemoryListCommand(int command)
    {
        using PrivilegeScope _ = PrivilegeScope.Enable(
            PrivilegeScope.ProfileSingleProcessPrivilege);
        int status = NativeMethods.NtSetSystemInformationInt(
            NativeMethods.SystemMemoryListInformation,
            ref command,
            sizeof(int));
        ThrowForNtStatus(status);
        return "Completed system memory-list command.";
    }

    private static string FlushSystemFileCache()
    {
        using PrivilegeScope _ = PrivilegeScope.Enable(
            PrivilegeScope.IncreaseQuotaPrivilege);
        SystemFileCacheInformation information = new()
        {
            MinimumWorkingSet = nuint.MaxValue,
            MaximumWorkingSet = nuint.MaxValue,
        };
        int status = NativeMethods.NtSetSystemInformationFileCache(
            NativeMethods.SystemFileCacheInformation,
            ref information,
            checked((uint)Marshal.SizeOf<SystemFileCacheInformation>()));
        ThrowForNtStatus(status);

        if (!NativeMethods.SetSystemFileCacheSize(
                nuint.MaxValue,
                nuint.MaxValue,
                0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return "Flushed the system file cache.";
    }

    private (string Message, ulong Released) TrimWorkingSets(
        MemoryOptimizerSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.GlobalWorkingSet)
        {
            if (!settings.GlobalWorkingSetWarningAccepted)
            {
                throw new InvalidOperationException(
                    "Global working-set mode requires explicit consent.");
            }

            return (ExecuteMemoryListCommand(
                NativeMethods.MemoryEmptyWorkingSets), 0);
        }

        using PrivilegeScope _ = PrivilegeScope.Enable(
            PrivilegeScope.DebugPrivilege);
        HashSet<string> exclusions = new(
            settings.ExcludedProcesses,
            StringComparer.OrdinalIgnoreCase);
        exclusions.Add("Dismode.MemoryService");
        exclusions.Add("Dismode.MemoryOptimizer");

        int trimmed = 0;
        int inaccessible = 0;
        int differentOwner = 0;
        ulong released = 0;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name;
                long before;
                try
                {
                    name = process.ProcessName;
                    before = process.WorkingSet64;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception)
                {
                    inaccessible++;
                    continue;
                }

                if (process.Id <= 4 || exclusions.Contains(name))
                {
                    continue;
                }

                using SafeProcessHandle handle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessSetQuota |
                        NativeMethods.ProcessQueryInformation |
                        NativeMethods.ProcessQueryLimitedInformation,
                    inheritHandle: false,
                    process.Id);
                if (handle.IsInvalid)
                {
                    inaccessible++;
                    continue;
                }

                if (!BelongsToUser(handle))
                {
                    differentOwner++;
                    continue;
                }

                if (!NativeMethods.K32EmptyWorkingSet(handle))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != NativeMethods.ErrorAccessDenied)
                    {
                        throw new Win32Exception(error, $"{name}: working-set trim failed.");
                    }

                    inaccessible++;
                    continue;
                }

                trimmed++;
                try
                {
                    process.Refresh();
                    long after = process.WorkingSet64;
                    if (before > after)
                    {
                        released = checked(released + (ulong)(before - after));
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or Win32Exception)
                {
                    inaccessible++;
                }
            }
        }

        return (
            $"Trimmed {trimmed} user process working sets; " +
                $"skipped {differentOwner} processes owned by other users " +
                $"and {inaccessible} inaccessible processes.",
            released);
    }

    private bool BelongsToUser(SafeProcessHandle processHandle)
    {
        if (!NativeMethods.OpenProcessToken(
                processHandle.DangerousGetHandle(),
                NativeMethods.TokenQuery,
                out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        using (WindowsIdentity identity = new(token.DangerousGetHandle()))
        {
            return identity.User is not null &&
                _userSid.Equals(identity.User);
        }
    }

    private static string FlushModifiedFileCache(
        CancellationToken cancellationToken)
    {
        List<string> warnings = [];
        int flushed = 0;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            string volumeName = "\\\\.\\" +
                drive.RootDirectory.FullName.TrimEnd(
                    Path.DirectorySeparatorChar);
            using SafeFileHandle handle = NativeMethods.CreateFile(
                volumeName,
                NativeMethods.GenericRead | NativeMethods.GenericWrite,
                NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                nint.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileAttributeNormal |
                    NativeMethods.FileFlagNoBuffering,
                nint.Zero);
            if (handle.IsInvalid)
            {
                warnings.Add($"{drive.Name}: {Marshal.GetLastPInvokeError()}");
                continue;
            }

            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlResetWriteOrder,
                    nint.Zero,
                    0,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero))
            {
                warnings.Add(
                    $"{drive.Name}: reset write order " +
                    Marshal.GetLastPInvokeError());
            }

            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlDiscardVolumeCache,
                    nint.Zero,
                    0,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero))
            {
                warnings.Add(
                    $"{drive.Name}: discard cache " +
                    Marshal.GetLastPInvokeError());
            }

            if (!NativeMethods.FlushFileBuffers(handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"{drive.Name}: volume cache flush failed.");
            }

            flushed++;
        }

        return $"Flushed {flushed} fixed volumes." +
            (warnings.Count == 0
                ? string.Empty
                : $" Optional controls reported {warnings.Count} warning(s).");
    }

    private static void ThrowForNtStatus(int status)
    {
        if (status == NativeMethods.ErrorSuccess)
        {
            return;
        }

        uint error = NativeMethods.RtlNtStatusToDosError(status);
        throw new Win32Exception(
            checked((int)error),
            $"NTSTATUS 0x{unchecked((uint)status):X8}.");
    }
}

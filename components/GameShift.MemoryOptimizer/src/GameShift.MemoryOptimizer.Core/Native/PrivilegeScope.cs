// SPDX-License-Identifier: GPL-3.0-only
// Derived from Windows Memory Cleaner 3.0.8 © Igor Mundstein.
// Modified to restore token state and reject ERROR_NOT_ALL_ASSIGNED.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GameShift.MemoryOptimizer.Core.Native;

internal sealed class PrivilegeScope : IDisposable
{
    internal const string DebugPrivilege = "SeDebugPrivilege";
    internal const string IncreaseQuotaPrivilege =
        "SeIncreaseQuotaPrivilege";
    internal const string ProfileSingleProcessPrivilege =
        "SeProfileSingleProcessPrivilege";

    private readonly SafeAccessTokenHandle _token;
    private readonly TokenPrivileges _previousState;
    private bool _disposed;

    private PrivilegeScope(
        SafeAccessTokenHandle token,
        TokenPrivileges previousState)
    {
        _token = token;
        _previousState = previousState;
    }

    internal static PrivilegeScope Enable(string privilegeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privilegeName);

        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TokenQuery |
                    NativeMethods.TokenAdjustPrivileges,
                out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(
                    null,
                    privilegeName,
                    out Luid luid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            TokenPrivileges requested = new()
            {
                PrivilegeCount = 1,
                Privileges = new()
                {
                    Luid = luid,
                    Attributes = NativeMethods.SePrivilegeEnabled,
                },
            };
            uint size = checked((uint)Marshal.SizeOf<TokenPrivileges>());
            Marshal.SetLastPInvokeError(NativeMethods.ErrorSuccess);
            bool adjusted = NativeMethods.AdjustTokenPrivileges(
                token,
                disableAllPrivileges: false,
                ref requested,
                size,
                out TokenPrivileges previous,
                out _);
            int error = Marshal.GetLastPInvokeError();
            if (!adjusted || error == NativeMethods.ErrorNotAllAssigned)
            {
                throw new Win32Exception(
                    error == NativeMethods.ErrorSuccess
                        ? NativeMethods.ErrorNotAllAssigned
                        : error,
                    $"Cannot enable {privilegeName}.");
            }

            return new(token, previous);
        }
        catch
        {
            token.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TokenPrivileges previous = _previousState;
        uint size = checked((uint)Marshal.SizeOf<TokenPrivileges>());
        Marshal.SetLastPInvokeError(NativeMethods.ErrorSuccess);
        bool restored = NativeMethods.AdjustTokenPrivileges(
            _token,
            disableAllPrivileges: false,
            ref previous,
            size,
            out _,
            out _);
        int error = Marshal.GetLastPInvokeError();
        if (!restored || error == NativeMethods.ErrorNotAllAssigned)
        {
            Trace.TraceError(
                "Memory Optimizer could not restore a token privilege: {0}.",
                error);
        }

        _token.Dispose();
    }
}

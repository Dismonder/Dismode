using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Dismode.Core.Domain.Processes;
using Dismode.Windows.NativeInterop;
using Microsoft.Win32.SafeHandles;

namespace Dismode.Windows.Processes;

public sealed class ProcessIdentityProvider : IProcessIdentityProvider
{
    public async ValueTask<ProcessIdentity?> TryCaptureAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return await TryCaptureAsync(process, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public async ValueTask<ProcessIdentity?> TryCaptureAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            ProcessRuntimeKey runtimeKey = ReadRuntimeKey(process);
            string executablePath = ReadExecutablePath(process);
            string userSid = ReadUserSid(process);
            int sessionId = process.SessionId;
            string executableSha256 = await ExecutableFileHasher.ComputeSha256Async(
                    executablePath,
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited
                || ReadRuntimeKey(process) != runtimeKey
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    ReadExecutablePath(process),
                    executablePath))
            {
                return null;
            }

            return new(
                runtimeKey,
                executablePath,
                executableSha256,
                publisher: null,
                userSid,
                sessionId,
                parentRuntimeKey: null);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or IOException
                or Win32Exception)
        {
            return null;
        }
    }

    public ValueTask<bool> MatchesRuntimeIdentityAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using Process process = Process.GetProcessById(
                expectedIdentity.RuntimeKey.ProcessId);
            ProcessRuntimeKey runtimeKey = ReadRuntimeKey(process);
            string executablePath = ReadExecutablePath(process);
            string userSid = ReadUserSid(process);
            int sessionId = process.SessionId;

            cancellationToken.ThrowIfCancellationRequested();
            bool matches =
                !process.HasExited
                && runtimeKey == expectedIdentity.RuntimeKey
                && sessionId == expectedIdentity.SessionId
                && StringComparer.OrdinalIgnoreCase.Equals(
                    executablePath,
                    expectedIdentity.ExecutablePath)
                && StringComparer.OrdinalIgnoreCase.Equals(
                    userSid,
                    expectedIdentity.UserSid)
                && ReadRuntimeKey(process) == runtimeKey
                && StringComparer.OrdinalIgnoreCase.Equals(
                    ReadExecutablePath(process),
                    executablePath);
            return ValueTask.FromResult(matches);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or NotSupportedException
                or UnauthorizedAccessException
                or IOException
                or Win32Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    private static ProcessRuntimeKey ReadRuntimeKey(Process process) =>
        new(
            process.Id,
            new DateTimeOffset(
                process.StartTime.ToUniversalTime(),
                TimeSpan.Zero));

    private static string ReadExecutablePath(Process process) =>
        ProcessImagePath.TryRead(process)
        ?? throw new InvalidOperationException(
            "The process executable path is unavailable.");

    private static string ReadUserSid(Process process)
    {
        if (ProcessNativeMethods.OpenProcessToken(
                process.SafeHandle,
                TokenAccessLevels.Query,
                out SafeAccessTokenHandle tokenHandle) == 0)
        {
            throw new Win32Exception();
        }

        using (tokenHandle)
        using (WindowsIdentity identity = new(tokenHandle.DangerousGetHandle()))
        {
            return identity.User?.Value
                ?? throw new InvalidOperationException(
                    "The process token does not contain a user SID.");
        }
    }

}

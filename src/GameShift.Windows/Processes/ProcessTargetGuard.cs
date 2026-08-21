using System.Diagnostics;
using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

internal static class ProcessTargetGuard
{
    internal static async ValueTask<Process> OpenValidatedAsync(
        ProcessIdentity expectedIdentity,
        IProcessIdentityProvider identityProvider,
        CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(
                expectedIdentity.RuntimeKey.ProcessId);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "The target process is no longer running.",
                exception);
        }

        try
        {
            ProcessIdentity? currentIdentity =
                await identityProvider
                    .TryCaptureAsync(process, cancellationToken)
                    .ConfigureAwait(false);

            if (currentIdentity is null
                || !expectedIdentity.MatchesExecutable(currentIdentity))
            {
                throw new InvalidOperationException(
                    "The target process identity changed; the operation was cancelled.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

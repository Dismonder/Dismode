using System.Diagnostics;
using GameShift.Core.Domain.Processes;

namespace GameShift.Windows.Processes;

public interface IProcessIdentityProvider
{
    ValueTask<ProcessIdentity?> TryCaptureAsync(
        int processId,
        CancellationToken cancellationToken);

    ValueTask<ProcessIdentity?> TryCaptureAsync(
        Process process,
        CancellationToken cancellationToken);

    ValueTask<bool> MatchesRuntimeIdentityAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken);
}

using GameShift.Core.Profiles;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

public sealed class AutoGameDetectionService
{
    private readonly IProcessInventory _processInventory;

    public AutoGameDetectionService(IProcessInventory? processInventory = null)
    {
        _processInventory = processInventory ?? new ProcessInventory();
    }

    public async ValueTask<AutoGameDetectionResult> ScanAsync(
        IGameProfileRepository repository,
        int currentSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        IReadOnlyList<ManualGameProfile> profiles =
            await repository.ListAsync(cancellationToken).ConfigureAwait(false);

        if (profiles.Count == 0)
        {
            return AutoGameDetectionResult.None;
        }

        Dictionary<string, ManualGameProfile> profileMap =
            profiles.ToDictionary(
                p => p.ExecutablePath,
                p => p,
                StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ProcessSnapshot> running = _processInventory.Capture();

        foreach (ProcessSnapshot process in running)
        {
            if (process.SessionId != currentSessionId
                || string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                continue;
            }

            if (profileMap.TryGetValue(process.ExecutablePath, out ManualGameProfile? matchedProfile))
            {
                return new AutoGameDetectionResult(
                    IsGameDetected: true,
                    DetectedGame: matchedProfile,
                    GameProcess: process);
            }
        }

        return AutoGameDetectionResult.None;
    }
}

public sealed record AutoGameDetectionResult(
    bool IsGameDetected,
    ManualGameProfile? DetectedGame,
    ProcessSnapshot? GameProcess)
{
    public static AutoGameDetectionResult None { get; } =
        new(false, null, null);
}

using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Profiles;

public interface IGameOptimizationPreferencesRepository
{
    ValueTask<GameOptimizationPreferences> LoadOptimizationPreferencesAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);
}

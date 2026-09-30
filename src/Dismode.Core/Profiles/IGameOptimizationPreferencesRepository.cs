using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Profiles;

public interface IGameOptimizationPreferencesRepository
{
    ValueTask<GameOptimizationPreferences> LoadOptimizationPreferencesAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);
}

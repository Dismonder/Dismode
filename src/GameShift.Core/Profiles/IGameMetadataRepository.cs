using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Profiles;

public interface IGameMetadataRepository
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GameMetadata>> ListMetadataAsync(
        CancellationToken cancellationToken);

    ValueTask<GameMetadata?> FindMetadataAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);

    ValueTask UpsertMetadataAsync(
        GameMetadata metadata,
        CancellationToken cancellationToken);
}

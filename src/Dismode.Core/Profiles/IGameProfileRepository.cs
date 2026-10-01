using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Profiles;

public interface IGameProfileRepository
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ManualGameProfile>> ListAsync(
        CancellationToken cancellationToken);

    ValueTask<ManualGameProfile?> FindAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);

    ValueTask UpsertAsync(
        ManualGameProfile profile,
        CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(
        GameProfileId profileId,
        CancellationToken cancellationToken);
}

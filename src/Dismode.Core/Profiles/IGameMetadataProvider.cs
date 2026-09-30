namespace Dismode.Core.Profiles;

public interface IGameMetadataProvider
{
    string Source { get; }

    ValueTask<GameMetadataProviderResult?> ReadAsync(
        ManualGameProfile profile,
        string? externalId,
        CancellationToken cancellationToken);
}

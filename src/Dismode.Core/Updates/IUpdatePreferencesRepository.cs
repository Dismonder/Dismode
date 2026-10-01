namespace Dismode.Core.Updates;

public interface IUpdatePreferencesRepository
{
    ValueTask<UpdatePreferences> LoadUpdatePreferencesAsync(
        CancellationToken cancellationToken);

    ValueTask SaveUpdatePreferencesAsync(
        UpdatePreferences preferences,
        CancellationToken cancellationToken);
}

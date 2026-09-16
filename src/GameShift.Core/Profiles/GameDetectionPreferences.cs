namespace GameShift.Core.Profiles;

/// <summary>
/// How GameShift reacts to a library game that starts outside GameShift.
/// </summary>
/// <param name="AutoOptimizeDetectedGames">
/// When true the UI attaches a session with the game's saved rules as soon
/// as the running game is detected, without a click and also while the
/// window sits in the tray. When false detection only shows the banner.
/// </param>
/// <param name="UpdatedAtUtc">When the user last changed the setting.</param>
public sealed record GameDetectionPreferences(
    bool AutoOptimizeDetectedGames,
    DateTimeOffset UpdatedAtUtc)
{
    public DateTimeOffset UpdatedAtUtc { get; init; } =
        UpdatedAtUtc.ToUniversalTime();

    /// <summary>
    /// Enabled by default: the automatic path applies exactly what the
    /// one-click banner applies, and a user who installs an optimizer
    /// expects it to act on the game they are playing.
    /// </summary>
    public static GameDetectionPreferences CreateDefault() =>
        new(
            AutoOptimizeDetectedGames: true,
            DateTimeOffset.UtcNow);
}

public interface IGameDetectionPreferencesRepository
{
    ValueTask<GameDetectionPreferences> LoadGameDetectionPreferencesAsync(
        CancellationToken cancellationToken);

    ValueTask SaveGameDetectionPreferencesAsync(
        GameDetectionPreferences preferences,
        CancellationToken cancellationToken);
}

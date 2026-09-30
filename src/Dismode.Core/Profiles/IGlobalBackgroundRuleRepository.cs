namespace Dismode.Core.Profiles;

/// <summary>
/// Background-process rules saved once for every game in the library: what
/// the automatic optimization closes or restrains whenever any detected
/// game starts. A rule saved for a specific game wins over the global one
/// for the same executable, so a per-game <see cref="SavedBackgroundActionMode.Ignore"/>
/// switches a global rule off for that title.
/// </summary>
public interface IGlobalBackgroundRuleRepository
{
    ValueTask<IReadOnlyList<SavedBackgroundProcessRule>>
        LoadGlobalBackgroundRulesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the whole set. An empty list clears it.
    /// </summary>
    ValueTask SaveGlobalBackgroundRulesAsync(
        IReadOnlyList<SavedBackgroundProcessRule> rules,
        CancellationToken cancellationToken);
}

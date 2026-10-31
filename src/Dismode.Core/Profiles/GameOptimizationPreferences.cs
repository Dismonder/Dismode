using System.Collections.ObjectModel;
using Dismode.Core.Domain.Identifiers;

namespace Dismode.Core.Profiles;

public enum SavedGamePriorityMode
{
    Normal = 1,
    AboveNormal = 2,
    High = 3,
}

public enum SavedBackgroundActionMode
{
    CloseAndRestore = 1,
    LowerPriority = 2,
    Ignore = 3,
    LowerPriorityAndEcoQos = 4,

    /// <summary>
    /// The full background bundle (corner affinity mask, EcoQoS, lowered
    /// memory and I/O priority). A value of its own so a rule saved as
    /// <see cref="LowerPriorityAndEcoQos"/> before the bundle existed keeps
    /// the meaning the user agreed to.
    /// </summary>
    RestrainBackground = 5,
}

public sealed record SavedBackgroundProcessRule
{
    public SavedBackgroundProcessRule(
        string executablePath,
        SavedBackgroundActionMode actionMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath)
            || !string.Equals(
                Path.GetExtension(executablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A background-process rule requires a fully qualified EXE path.",
                nameof(executablePath));
        }

        if (!Enum.IsDefined(actionMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(actionMode),
                actionMode,
                "The background-process action is not recognized.");
        }

        ExecutablePath = Path.GetFullPath(executablePath);
        ActionMode = actionMode;
    }

    public string ExecutablePath { get; }

    public SavedBackgroundActionMode ActionMode { get; }
}

public sealed record GameOptimizationPreferences
{
    private const int MaximumRuleCount = 128;

    public GameOptimizationPreferences(
        GameProfileId profileId,
        SavedGamePriorityMode gamePriority,
        IEnumerable<SavedBackgroundProcessRule>? backgroundRules,
        DateTimeOffset updatedAtUtc,
        bool autoOptimizeWhenDetected = true,
        bool pinGameToPreferredCores = true,
        bool keepAwakeDuringSession = true,
        bool applyBackgroundRulesDuringSession = false,
        bool maximizeRefreshRateDuringSession = false)
    {
        if (!Enum.IsDefined(gamePriority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(gamePriority),
                gamePriority,
                "The game-priority preference is not recognized.");
        }

        SavedBackgroundProcessRule[] rules =
            backgroundRules?.ToArray() ?? [];
        if (rules.Length > MaximumRuleCount)
        {
            throw new ArgumentException(
                $"No more than {MaximumRuleCount} rules can be saved per game.",
                nameof(backgroundRules));
        }

        if (rules
            .GroupBy(
                rule => rule.ExecutablePath,
                StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException(
                "A game cannot contain duplicate background-process rules.",
                nameof(backgroundRules));
        }

        ProfileId = profileId;
        GamePriority = gamePriority;
        BackgroundRules =
            new ReadOnlyCollection<SavedBackgroundProcessRule>(rules);
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        AutoOptimizeWhenDetected = autoOptimizeWhenDetected;
        PinGameToPreferredCores = pinGameToPreferredCores;
        KeepAwakeDuringSession = keepAwakeDuringSession;
        ApplyBackgroundRulesDuringSession = applyBackgroundRulesDuringSession;
        MaximizeRefreshRateDuringSession = maximizeRefreshRateDuringSession;
    }

    public GameProfileId ProfileId { get; }

    public SavedGamePriorityMode GamePriority { get; }

    public IReadOnlyList<SavedBackgroundProcessRule> BackgroundRules { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>
    /// Whether the automatic optimization may attach a session when this
    /// game is detected running outside Dismode. Off means banner only.
    /// The same per-game escape hatch as GameMode's blacklist or Special K's
    /// per-game injection toggle: a global automat needs a way to leave one
    /// title alone without being switched off for everything.
    /// </summary>
    public bool AutoOptimizeWhenDetected { get; }

    /// <summary>
    /// Whether a session may pin this game to the machine's performance cores
    /// (CPU sets, or a hard mask where sets cannot work). Independent of the
    /// background restraint: off leaves the game process untouched while
    /// ProBalance keeps working on everything else. On by default, so rows
    /// saved before this switch existed behave as they always did.
    /// </summary>
    public bool PinGameToPreferredCores { get; }

    /// <summary>
    /// Whether a session asks Windows to keep the display and the system
    /// awake while it runs (a power request, released when the session ends).
    /// For games played with a pad alone and for long cutscenes, which give
    /// the system no input to count as activity. On by default.
    /// </summary>
    public bool KeepAwakeDuringSession { get; }

    public bool ApplyBackgroundRulesDuringSession { get; }

    public bool MaximizeRefreshRateDuringSession { get; }

    public static GameOptimizationPreferences CreateDefault(
        GameProfileId profileId) =>
        new(
            profileId,
            SavedGamePriorityMode.Normal,
            backgroundRules: null,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// The rules of this game plus the rules saved for every game. A rule of
    /// this game wins for the same executable, including an explicit
    /// <see cref="SavedBackgroundActionMode.Ignore"/>, which switches the
    /// global rule off for this title. The result never exceeds the per-game
    /// limit: global rules beyond it are dropped in path order instead of
    /// failing the whole plan.
    /// </summary>
    public GameOptimizationPreferences WithGlobalRules(
        IEnumerable<SavedBackgroundProcessRule> globalRules)
    {
        ArgumentNullException.ThrowIfNull(globalRules);
        List<SavedBackgroundProcessRule> merged = new(BackgroundRules);
        HashSet<string> covered = new(
            BackgroundRules.Select(rule => rule.ExecutablePath),
            StringComparer.OrdinalIgnoreCase);
        foreach (SavedBackgroundProcessRule rule in globalRules
                     .OrderBy(
                         rule => rule.ExecutablePath,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (merged.Count >= MaximumRuleCount)
            {
                break;
            }

            if (covered.Add(rule.ExecutablePath))
            {
                merged.Add(rule);
            }
        }

        return merged.Count == BackgroundRules.Count
            ? this
            : new(
                ProfileId,
                GamePriority,
                merged,
                UpdatedAtUtc,
                AutoOptimizeWhenDetected,
                PinGameToPreferredCores,
                KeepAwakeDuringSession,
                ApplyBackgroundRulesDuringSession,
                MaximizeRefreshRateDuringSession);
    }
}

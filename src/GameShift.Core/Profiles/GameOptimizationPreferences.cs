using System.Collections.ObjectModel;
using GameShift.Core.Domain.Identifiers;

namespace GameShift.Core.Profiles;

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
        DateTimeOffset updatedAtUtc)
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
    }

    public GameProfileId ProfileId { get; }

    public SavedGamePriorityMode GamePriority { get; }

    public IReadOnlyList<SavedBackgroundProcessRule> BackgroundRules { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static GameOptimizationPreferences CreateDefault(
        GameProfileId profileId) =>
        new(
            profileId,
            SavedGamePriorityMode.Normal,
            backgroundRules: null,
            DateTimeOffset.UtcNow);
}

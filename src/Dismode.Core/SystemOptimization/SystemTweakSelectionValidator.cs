using Dismode.Contracts.SystemOptimization;

namespace Dismode.Core.SystemOptimization;

public sealed class SystemTweakSelectionValidator
{
    public const string DangerousConfirmationText = "ROZUMIEM RYZYKO";

    private readonly Dictionary<string, TweakDefinition> _catalog;

    public SystemTweakSelectionValidator(
        IEnumerable<TweakDefinition> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        TweakDefinition[] definitions = [.. catalog];
        if (definitions.Length == 0)
        {
            throw new ArgumentException(
                "The system tweak catalog cannot be empty.",
                nameof(catalog));
        }

        _catalog = definitions.ToDictionary(
            definition => definition.Id,
            StringComparer.Ordinal);
    }

    public SystemTweakValidationResult Validate(
        IReadOnlyList<TweakSelection> selections,
        bool allowDangerous)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0)
        {
            return SystemTweakValidationResult.Rejected(
                "At least one tweak selection is required.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<TweakDefinition> resolved = [];
        foreach (TweakSelection selection in selections)
        {
            if (!seen.Add(selection.TweakId))
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{selection.TweakId}' was selected more than once.");
            }

            if (!_catalog.TryGetValue(selection.TweakId, out TweakDefinition? definition))
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{selection.TweakId}' is not present in the server catalog.");
            }

            if (definition.Revision != selection.Revision)
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{selection.TweakId}' has a stale catalog revision.");
            }

            if (definition.Availability != SystemTweakAvailability.Supported
                || string.IsNullOrWhiteSpace(definition.ExecutionAdapterId))
            {
                return SystemTweakValidationResult.Rejected(
                    definition.BlockingReason
                    ?? $"Tweak '{selection.TweakId}' is unsupported on this release.");
            }

            if (!definition.AllowedValues.Contains(
                    selection.Value,
                    StringComparer.Ordinal))
            {
                return SystemTweakValidationResult.Rejected(
                    $"Value '{selection.Value}' is not allowed for tweak '{selection.TweakId}'.");
            }

            if (definition.RequiresTarget
                && !IsValidOpaqueTarget(selection.TargetId))
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{selection.TweakId}' requires a bounded server inventory target ID.");
            }

            if (!definition.RequiresTarget
                && !string.IsNullOrWhiteSpace(selection.TargetId))
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{selection.TweakId}' does not accept a target ID.");
            }

            if (definition.Risk == SystemTweakRisk.Dangerous)
            {
                if (!allowDangerous
                    || !StringComparer.Ordinal.Equals(
                        selection.DangerousConfirmation,
                        DangerousConfirmationText))
                {
                    return SystemTweakValidationResult.Rejected(
                        $"Tweak '{selection.TweakId}' requires an individual danger confirmation.");
                }

                if (selections.Count != 1)
                {
                    return SystemTweakValidationResult.Rejected(
                        "Dangerous tweaks must be prepared and executed one at a time.");
                }
            }

            resolved.Add(definition);
        }

        foreach (TweakDefinition definition in resolved)
        {
            string? conflict = definition.ConflictsWith.FirstOrDefault(seen.Contains);
            if (conflict is not null)
            {
                return SystemTweakValidationResult.Rejected(
                    $"Tweak '{definition.Id}' conflicts with '{conflict}'.");
            }
        }

        return SystemTweakValidationResult.Valid(resolved);
    }

    private static bool IsValidOpaqueTarget(string? targetId) =>
        !string.IsNullOrWhiteSpace(targetId)
        && targetId.Length <= 128
        && targetId.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');
}

public sealed record SystemTweakValidationResult(
    bool IsValid,
    string? Error,
    IReadOnlyList<TweakDefinition> Definitions)
{
    public static SystemTweakValidationResult Valid(
        IReadOnlyList<TweakDefinition> definitions) =>
        new(true, null, definitions);

    public static SystemTweakValidationResult Rejected(string error) =>
        new(false, error, []);
}

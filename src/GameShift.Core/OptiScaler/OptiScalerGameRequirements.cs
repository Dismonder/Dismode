namespace GameShift.Core.OptiScaler;

/// <summary>
/// An extra dependency a specific game needs before OptiScaler can run in it.
/// GameShift never installs these itself: they are third-party components,
/// and in the RE Engine case the companion works by bypassing the game's DRM.
/// Installing that on someone's behalf is their decision, not ours.
/// </summary>
public sealed record OptiScalerGameRequirement(
    string ExecutableName,
    string GameName,
    string CompanionName,
    string CompanionFileName,
    OptiScalerProxy RequiredProxy,
    string Notice,
    string ReferenceUrl);

public static class OptiScalerGameRequirements
{
    /// <summary>
    /// Games known to need a companion before OptiScaler works. Sourced from
    /// the OptiScaler project wiki; without the companion the game crashes on
    /// launch rather than falling back, so this is a hard requirement.
    /// </summary>
    private static readonly OptiScalerGameRequirement[] Known =
    [
        new(
            "re9.exe",
            "RESIDENT EVIL Requiem",
            "REFramework",
            "dinput8.dll",
            OptiScalerProxy.Dxgi,
            "OptiScaler w tej grze wymaga REFramework zainstalowanego jako "
                + "dinput8.dll w katalogu gry. Bez niego gra kończy się "
                + "błędem zaraz po uruchomieniu. Po instalacji zmień w "
                + "REFramework skrót overlaya z Insert na Delete, bo Insert "
                + "jest zajęty przez overlay OptiScalera.",
            "https://github.com/optiscaler/OptiScaler/wiki/Resident-Evil-9-Requiem"),
    ];

    public static OptiScalerGameRequirement? Find(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fileName = Path.GetFileName(executablePath);
        return Known.FirstOrDefault(requirement => string.Equals(
            requirement.ExecutableName,
            fileName,
            StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the companion is already sitting next to the game.
    /// </summary>
    public static bool IsSatisfied(
        OptiScalerGameRequirement requirement,
        IEnumerable<string> gameDirectoryFileNames)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(gameDirectoryFileNames);

        return gameDirectoryFileNames.Any(name => string.Equals(
            Path.GetFileName(name),
            requirement.CompanionFileName,
            StringComparison.OrdinalIgnoreCase));
    }
}

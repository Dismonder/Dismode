namespace GameShift.Core.OptiScaler;

/// <summary>
/// What a particular game needs before OptiScaler runs in it without crashing:
/// an extra component, a specific proxy DLL name, or settings that differ from
/// the package defaults.
/// GameShift never installs a companion itself: those are third-party
/// components, and in the RE Engine case the companion works by bypassing the
/// game's DRM. Installing that on someone's behalf is their decision, not ours.
/// Settings, by contrast, belong to OptiScaler's own configuration file and are
/// applied during installation.
/// </summary>
public sealed record OptiScalerGameRequirement(
    string ExecutableName,
    string GameName,
    OptiScalerProxy RequiredProxy,
    string Notice,
    string ReferenceUrl,
    string? CompanionName = null,
    string? CompanionFileName = null,
    IReadOnlyList<OptiScalerIniSetting>? Settings = null)
{
    public IReadOnlyList<OptiScalerIniSetting> RequiredSettings =>
        Settings ?? [];

    public bool NeedsCompanion => CompanionFileName is not null;
}

public static class OptiScalerGameRequirements
{
    /// <summary>
    /// RE Engine titles crash on launch when OptiScaler spoofs the adapter
    /// through DXGI, so spoofing goes off. They also need REFramework, which
    /// opens its own overlay on Insert — the same key OptiScaler defaults to.
    /// Two overlays on one key leaves neither usable, so OptiScaler moves to
    /// Home instead of asking the user to reconfigure the other mod.
    /// </summary>
    private static readonly OptiScalerIniSetting[] ReEngineSettings =
    [
        new("Spoofing", "Dxgi", "false"),
        new("Menu", "ShortcutKey", "0x24"),
    ];

    private const string ReEngineNotice =
        "Ta gra wymaga REFramework zainstalowanego jako dinput8.dll "
        + "w katalogu gry — bez niego kończy się błędem zaraz po "
        + "uruchomieniu. GameShift nie pobiera go za Ciebie, bo to "
        + "komponent zewnętrzny omijający zabezpieczenia gry. Sam "
        + "OptiScaler zostanie skonfigurowany pod ten silnik: spoofing "
        + "DXGI wyłączony (inaczej gra się wywala) i overlay przeniesiony "
        + "z Insert na Home, żeby nie kolidował z REFramework.";

    /// <summary>
    /// Games known to need something beyond the defaults. Sourced from the
    /// OptiScaler project wiki. Where a companion is listed, its absence ends
    /// in a crash rather than a graceful fallback, so it is a hard requirement.
    /// </summary>
    private static readonly OptiScalerGameRequirement[] Known =
    [
        new(
            "re9.exe",
            "RESIDENT EVIL Requiem",
            OptiScalerProxy.Dxgi,
            ReEngineNotice,
            "https://github.com/optiscaler/OptiScaler/wiki/Resident-Evil-9-Requiem",
            CompanionName: "REFramework",
            CompanionFileName: "dinput8.dll",
            Settings: ReEngineSettings),
        new(
            "MonsterHunterWilds.exe",
            "Monster Hunter Wilds",
            OptiScalerProxy.Dxgi,
            ReEngineNotice,
            "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List",
            CompanionName: "REFramework",
            CompanionFileName: "dinput8.dll",
            Settings: ReEngineSettings),
        new(
            "Forspoken.exe",
            "Forspoken",
            OptiScalerProxy.D3d12,
            "Forspoken ładuje OptiScaler wyłącznie pod nazwą d3d12.dll. "
                + "Przy innym proxy mod się nie uruchomi.",
            "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List"),
        new(
            "D2R.exe",
            "Diablo II: Resurrected",
            OptiScalerProxy.Winmm,
            "Diablo II: Resurrected ładuje OptiScaler wyłącznie pod nazwą "
                + "winmm.dll. Przy innym proxy mod się nie uruchomi.",
            "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List"),
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
    /// True when the companion is already sitting next to the game, or when the
    /// game does not need one.
    /// </summary>
    public static bool IsSatisfied(
        OptiScalerGameRequirement requirement,
        IEnumerable<string> gameDirectoryFileNames)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentNullException.ThrowIfNull(gameDirectoryFileNames);

        if (requirement.CompanionFileName is not { } companion)
        {
            return true;
        }

        return gameDirectoryFileNames.Any(name => string.Equals(
            Path.GetFileName(name),
            companion,
            StringComparison.OrdinalIgnoreCase));
    }
}

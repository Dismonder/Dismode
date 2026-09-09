namespace GameShift.Core.OptiScaler;

/// <summary>
/// What a particular game needs before OptiScaler runs in it without crashing:
/// an extra component, a specific proxy DLL name, or settings that differ from
/// the package defaults.
/// Where a companion is marked auto-installable, GameShift can fetch it: that
/// is currently REFramework, praydog's MIT-licensed mod loader for RE Engine
/// games, pinned to one build and checked against its SHA-256. It is still only
/// fetched when the user asks for it. Settings belong to OptiScaler's own
/// configuration file and are applied during installation.
/// </summary>
public sealed record OptiScalerGameRequirement(
    string ExecutableName,
    string GameName,
    OptiScalerProxy RequiredProxy,
    string Notice,
    string ReferenceUrl,
    string? CompanionName = null,
    string? CompanionFileName = null,
    IReadOnlyList<OptiScalerIniSetting>? Settings = null,
    bool CompanionAutoInstallable = false,
    IReadOnlyList<string>? ConflictingFileNames = null)
{
    public IReadOnlyList<OptiScalerIniSetting> RequiredSettings =>
        Settings ?? [];

    public bool NeedsCompanion => CompanionFileName is not null;

    /// <summary>
    /// Files whose presence means another loader is already hooking the game
    /// the same way. Two of them fighting over one entry point is the crash
    /// this layout exists to avoid. No game declares one today — REFramework
    /// installs under its own dinput8.dll name — but installs made by earlier
    /// GameShift versions did move a file aside, and uninstall still has to
    /// put those back.
    /// </summary>
    public IReadOnlyList<string> Conflicts => ConflictingFileNames ?? [];
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
        "Ta gra wymaga REFramework — bez niego OptiScaler nie wstaje. "
        + "GameShift instaluje go dokładnie tak, jak każe instrukcja samego "
        + "REFramework: jako dinput8.dll obok gry. Do tego spoofing DXGI "
        + "wyłączony, bo silnik RE się przy nim wywala, i overlay "
        + "OptiScalera przeniesiony z Insert na Home, żeby nie kolidował z "
        + "overlayem REFramework. Sam REFramework to otwarty projekt "
        + "praydoga na licencji MIT, przypięty do jednego wydania "
        + "i sprawdzany sumą kontrolną.";

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
            Settings: ReEngineSettings,
            CompanionAutoInstallable: true),
        new(
            "MonsterHunterWilds.exe",
            "Monster Hunter Wilds",
            OptiScalerProxy.Dxgi,
            ReEngineNotice,
            "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List",
            CompanionName: "REFramework",
            CompanionFileName: "dinput8.dll",
            Settings: ReEngineSettings,
            CompanionAutoInstallable: true),
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
        new(
            "DOA6.exe",
            "DEAD OR ALIVE 6: Last Round",
            OptiScalerProxy.D3d12,
            "DEAD OR ALIVE 6 ładuje OptiScaler jako d3d12.dll albo "
                + "version.dll. Przy dxgi.dll mod się nie uruchomi.",
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

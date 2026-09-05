using GameShift.Contracts.SystemOptimization;
using GameShift.Core.SystemOptimization;

namespace GameShift.UnitTests.SystemOptimization;

[TestClass]
public sealed class SystemTweakCatalogTests
{
    private static readonly string[] RequiredCategories =
    [
        "Procesy",
        "CPU i zasilanie",
        "Scheduler i MMCSS",
        "GPU i DWM",
        "Tryb gry i przechwytywanie",
        "Sieć",
        "Pamięć",
        "System plików",
        "Usługi i zadania",
        "Responsywność UI",
        "Hard Safety Policy",
    ];

    [TestMethod]
    public void DefaultCatalogHasStableUniqueIdentifiersAndAllCategories()
    {
        IReadOnlyList<TweakDefinition> catalog =
            SystemTweakCatalog.CreateDefault();

        Assert.AreEqual(
            catalog.Count,
            catalog.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.IsSubsetOf(
            RequiredCategories,
            catalog.Select(item => item.Category).Distinct().ToArray());
        Assert.IsTrue(catalog.All(item => item.Revision > 0));
        Assert.IsTrue(catalog.All(item => !string.IsNullOrWhiteSpace(item.TechnicalSource)));
        Assert.IsTrue(catalog.All(item => !string.IsNullOrWhiteSpace(item.RestoreDescription)));
    }

    [TestMethod]
    public void HardSafetyTargetsAreVisibleButNeverExecutable()
    {
        Dictionary<string, TweakDefinition> catalog =
            SystemTweakCatalog.CreateDefault().ToDictionary(item => item.Id);
        string[] blockedIds =
        [
            "security.defender.disable",
            "security.firewall.disable",
            "security.windows-update.disable",
            "security.bitlocker.disable",
            "security.tamper-protection.disable",
            "security.secure-boot.disable",
            "security.hvci.disable",
            "reliability.whea.disable",
            "reliability.dpc-watchdog.disable",
            "timer.bcd.useplatformclock",
            "timer.bcd.disabledynamictick",
        ];

        foreach (string id in blockedIds)
        {
            TweakDefinition definition = catalog[id];
            Assert.AreEqual(SystemTweakRisk.Blocked, definition.Risk, id);
            Assert.AreEqual(SystemTweakAvailability.BlockedByPolicy, definition.Availability, id);
            Assert.IsNull(definition.ExecutionAdapterId, id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(definition.BlockingReason), id);
        }
    }

    [TestMethod]
    public void SelectionValidatorAcceptsOnlyCatalogValuesAndPerItemDangerConsent()
    {
        SystemTweakSelectionValidator validator = new(
            SystemTweakCatalog.CreateDefault());
        TweakDefinition dangerous = SystemTweakCatalog.CreateDefault()
            .Single(item =>
                item.Risk == SystemTweakRisk.Dangerous
                && item.Availability == SystemTweakAvailability.Supported);

        TweakSelection valid = new(
            dangerous.Id,
            dangerous.Revision,
            dangerous.AllowedValues[0],
            DangerousConfirmation: SystemTweakSelectionValidator.DangerousConfirmationText);
        TweakSelection missingConsent = valid with { DangerousConfirmation = null };
        TweakSelection injectedValue = valid with { Value = "powershell.exe -Command Disable-Security" };

        Assert.IsTrue(validator.Validate([valid], allowDangerous: true).IsValid);
        Assert.IsFalse(validator.Validate([missingConsent], allowDangerous: true).IsValid);
        Assert.IsFalse(validator.Validate([injectedValue], allowDangerous: true).IsValid);
        Assert.IsFalse(validator.Validate([valid, valid], allowDangerous: true).IsValid);
    }

    [TestMethod]
    public void SupportedDefinitionsAlwaysUseFixedAdaptersAndBoundedValues()
    {
        IReadOnlyList<TweakDefinition> supported = SystemTweakCatalog
            .CreateDefault()
            .Where(item => item.Availability == SystemTweakAvailability.Supported)
            .ToArray();

        Assert.IsNotEmpty(supported);
        Assert.IsTrue(supported.All(item => !string.IsNullOrWhiteSpace(item.ExecutionAdapterId)));
        Assert.IsTrue(supported.All(item => item.AllowedValues.Count is > 0 and <= 16));
        Assert.IsFalse(supported.SelectMany(item => item.AllowedValues).Any(value =>
            value.Contains("powershell", StringComparison.OrdinalIgnoreCase)
            || value.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || value.Contains('\\')
            || value.Contains('/')));
    }
}

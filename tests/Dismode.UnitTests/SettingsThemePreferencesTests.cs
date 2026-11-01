using Dismode.UI.Services;

namespace Dismode.UnitTests;

[TestClass]
public sealed class SettingsThemePreferencesTests
{
    [TestMethod]
    public void ThemeChoiceSurvivesANewPreferencesInstance()
    {
        string? stored = null;
        foreach (string theme in new[] { "Dark", "Light", "Default" })
        {
            SettingsThemePreferences preferences = new(() => stored, value => stored = value);
            preferences.Save(theme);
            Assert.AreEqual(theme, new SettingsThemePreferences(() => stored, _ => { }).Load());
        }
    }

    [TestMethod]
    public void MissingOrUnknownThemeUsesSystemDefault()
    {
        foreach (string? stored in new[] { null, string.Empty, "unknown" })
        {
            Assert.AreEqual("Default", new SettingsThemePreferences(() => stored, _ => { }).Load());
        }
    }

    [TestMethod]
    public void UnavailableStorageDoesNotBreakThemeSelection()
    {
        SettingsThemePreferences preferences = new(
            () => throw new IOException(), _ => throw new UnauthorizedAccessException());
        Assert.AreEqual("Default", preferences.Load());
        preferences.Save("Light");
        Assert.ThrowsExactly<ArgumentException>(() => preferences.Save("unknown"));
    }
}

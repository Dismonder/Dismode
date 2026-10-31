using Dismode.Core.Profiles;

namespace Dismode.UnitTests.Profiles;

[TestClass]
public sealed class PerformanceOverlayPreferencesTests
{
    [TestMethod]
    [DataRow(1, "FullDeck")]
    [DataRow(2, "CompactBar")]
    [DataRow(3, "MinimalText")]
    [DataRow(4, "FpsAndLows")]
    public void PersistedStyleValuesKeepTheirMeaning(int value, string name)
    {
        PerformanceOverlayPreferences preferences = new(
            true, 94, 100, PerformanceOverlayCorner.TopRight,
            (PerformanceOverlayStyle)value, PerformanceOverlayTheme.CyberNeon,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(name, preferences.Style.ToString());
    }

    [TestMethod]
    public void DefaultStyleIsFullDeck() =>
        Assert.AreEqual(PerformanceOverlayStyle.FullDeck,
            PerformanceOverlayPreferences.CreateDefault().Style);

    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void UnknownStylesAreRejected(int value) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PerformanceOverlayPreferences(
                true, 94, 100, PerformanceOverlayCorner.TopRight,
                (PerformanceOverlayStyle)value, PerformanceOverlayTheme.CyberNeon,
                DateTimeOffset.UtcNow));
}

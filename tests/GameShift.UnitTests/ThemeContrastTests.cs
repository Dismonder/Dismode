using System.Globalization;
using System.Text.RegularExpressions;

namespace GameShift.UnitTests;

/// <summary>
/// Checks the palette against WCAG contrast, because "looks fine on my
/// monitor" is not a measurement.
/// <para>
/// This was written after finding that the frame-time chart's axis labels —
/// "20 ms", "10 ms", "0 ms" at ten pixels — sat at a contrast ratio of 2,18
/// against the card they are drawn on, where 4,5 is the minimum for text that
/// size. The scale of a chart is not decoration: without it the drawing means
/// nothing. Nobody had noticed because nobody had measured.
/// </para>
/// </summary>
[TestClass]
public sealed class ThemeContrastTests
{
    /// <summary>
    /// Minimum for normal-size text. Large text is allowed 3,0, but nothing in
    /// this palette is used exclusively at large sizes, so the strict bar
    /// applies everywhere.
    /// </summary>
    private const double MinimumContrast = 4.5;

    /// <summary>
    /// Roughly what sits behind a card: the window gradient. Surfaces are
    /// translucent, so their effective colour depends on it.
    /// </summary>
    private const string PageBackground = "FF0F1216";

    private static readonly string[] TextKeys =
    [
        "GameShiftPrimaryTextBrush",
        "GameShiftSecondaryTextBrush",
        "GameShiftTertiaryTextBrush",
        "GameShiftMutedTextBrush",
        "GameShiftSubtleTextBrush",
        "GameShiftAccentBrush",
        "GameShiftSuccessBrush",
        "GameShiftAmberBrush",
        "GameShiftDangerBrush",
    ];

    private static readonly string[] SurfaceKeys =
    [
        "GameShiftCardBrush",
        "GameShiftPanelBrush",
        "GameShiftNavigationBrush",
    ];

    [TestMethod]
    public void EveryTextColourIsReadableOnEverySurface()
    {
        Dictionary<string, string> palette = ReadPalette();
        List<string> failures = [];

        foreach (string text in TextKeys)
        {
            foreach (string surface in SurfaceKeys)
            {
                Assert.IsTrue(
                    palette.ContainsKey(text),
                    $"Brak koloru {text} w App.xaml.");
                Assert.IsTrue(
                    palette.ContainsKey(surface),
                    $"Brak koloru {surface} w App.xaml.");

                (byte R, byte G, byte B) background =
                    Composite(palette[surface], PageBackground);
                double contrast = Contrast(
                    Composite(palette[text], Opaque(background)),
                    background);
                if (contrast < MinimumContrast)
                {
                    failures.Add(
                        $"{text} na {surface}: "
                        + contrast.ToString("F2", CultureInfo.InvariantCulture)
                        + $" (wymagane {MinimumContrast})");
                }
            }
        }

        Assert.AreEqual(
            0,
            failures.Count,
            "Kolory poniżej progu czytelności:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// Both dictionaries have to agree. A palette that drifts between them
    /// shows one set of colours until the user changes the system theme and a
    /// different, untested set afterwards.
    /// </summary>
    [TestMethod]
    public void DefaultAndDarkDictionariesDefineTheSameColours()
    {
        string xaml = ReadAppXaml();
        int darkStart = xaml.IndexOf(
            "x:Key=\"Dark\"",
            StringComparison.Ordinal);
        Assert.IsGreaterThan(0, darkStart, "Brak słownika Dark w App.xaml.");

        Dictionary<string, string> light =
            ParseColours(xaml[..darkStart]);
        Dictionary<string, string> dark =
            ParseColours(xaml[darkStart..]);

        List<string> differences = [.. light
            .Where(entry => dark.TryGetValue(entry.Key, out string? other)
                && !string.Equals(entry.Value, other, StringComparison.Ordinal))
            .Select(entry =>
                $"{entry.Key}: {entry.Value} vs {dark[entry.Key]}")];

        Assert.AreEqual(
            0,
            differences.Count,
            "Słowniki różnią się kolorami:\n" + string.Join("\n", differences));
    }

    private static Dictionary<string, string> ReadPalette() =>
        ParseColours(ReadAppXaml());

    private static Dictionary<string, string> ParseColours(
        string xaml)
    {
        Dictionary<string, string> colours = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            xaml,
            "x:Key=\"([A-Za-z]+)\"\\s+Color=\"#([0-9A-Fa-f]{6,8})\""))
        {
            colours.TryAdd(
                match.Groups[1].Value,
                match.Groups[2].Value.ToUpperInvariant());
        }

        return colours;
    }

    private static string ReadAppXaml()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Nie znaleziono katalogu repozytorium.");
        return File.ReadAllText(Path.Combine(
            directory.FullName,
            "src",
            "GameShift.UI",
            "App.xaml"));
    }

    private static string Opaque((byte R, byte G, byte B) colour) =>
        $"FF{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>
    /// Flattens a translucent colour onto an opaque one. Every surface in this
    /// palette has an alpha channel, so comparing the declared values directly
    /// would measure colours nobody ever sees.
    /// </summary>
    private static (byte R, byte G, byte B) Composite(
        string foreground,
        string background)
    {
        (double alpha, byte r, byte g, byte b) front = Split(foreground);
        (double _, byte br, byte bg, byte bb) = Split(background);
        return (
            Blend(front.r, br, front.alpha),
            Blend(front.g, bg, front.alpha),
            Blend(front.b, bb, front.alpha));

        static byte Blend(byte front, byte back, double alpha) =>
            (byte)Math.Round((front * alpha) + (back * (1 - alpha)));
    }

    private static (double Alpha, byte R, byte G, byte B) Split(string colour)
    {
        return colour.Length == 8
            ? (Byte(colour, 0) / 255d,
                Byte(colour, 2),
                Byte(colour, 4),
                Byte(colour, 6))
            : (1d, Byte(colour, 0), Byte(colour, 2), Byte(colour, 4));

        static byte Byte(string value, int index) => byte.Parse(
            value.AsSpan(index, 2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
    }

    private static double Contrast(
        (byte R, byte G, byte B) first,
        (byte R, byte G, byte B) second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return a < b
            ? (b + 0.05) / (a + 0.05)
            : (a + 0.05) / (b + 0.05);
    }

    private static double RelativeLuminance((byte R, byte G, byte B) colour) =>
        (0.2126 * Channel(colour.R))
        + (0.7152 * Channel(colour.G))
        + (0.0722 * Channel(colour.B));

    private static double Channel(byte value)
    {
        double scaled = value / 255d;
        return scaled <= 0.03928
            ? scaled / 12.92
            : Math.Pow((scaled + 0.055) / 1.055, 2.4);
    }
}

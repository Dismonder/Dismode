using System.Xml.Linq;

namespace GameShift.UnitTests;

/// <summary>
/// Every control a person can operate has to announce what it is.
/// <para>
/// A tooltip is not enough: Narrator reads the control's name, and an
/// icon-only button without one is announced as "button" and nothing else.
/// Six controls were in that state when this was written — the game rail, two
/// icon buttons, the service list and the two sound and animation switches,
/// whose labels sat in a neighbouring TextBlock that nothing connected to
/// them.
/// </para>
/// <para>
/// Written after three cruder greps over the same file produced three wrong
/// answers in a row, the last of which claimed thirty failures where there
/// were six. The difference is that this reads the markup as a tree, so a
/// button whose label is a child element counts as labelled.
/// </para>
/// </summary>
[TestClass]
public sealed class AccessibleNameTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Controls a keyboard or screen-reader user can land on and activate.
    /// Static text is deliberately absent — it is read as content, not
    /// announced as a control.
    /// </summary>
    private static readonly string[] InteractiveElements =
    [
        "Button",
        "ToggleButton",
        "ToggleSwitch",
        "ComboBox",
        "CheckBox",
        "ListView",
        "GridView",
        "Slider",
        "HyperlinkButton",
    ];

    /// <summary>Attributes that put a readable label on the control itself.</summary>
    private static readonly string[] LabelAttributes =
    [
        "Text",
        "Content",
        "Header",
    ];

    [TestMethod]
    [DataRow("MainWindow.xaml")]
    [DataRow("PerformanceOverlayWindow.xaml")]
    public void EveryInteractiveControlAnnouncesWhatItIs(string fileName)
    {
        XDocument document = XDocument.Load(UiFilePath(fileName));
        List<string> unnamed = [];

        foreach (XElement element in document.Descendants()
            .Where(candidate => InteractiveElements.Contains(
                candidate.Name.LocalName,
                StringComparer.Ordinal)))
        {
            if (HasAutomationName(element) || HasVisibleLabel(element))
            {
                continue;
            }

            string? name = (string?)element.Attribute(Xaml + "Name");
            unnamed.Add(
                $"{element.Name.LocalName} {name ?? "(bez x:Name)"}");
        }

        Assert.AreEqual(
            0,
            unnamed.Count,
            $"W {fileName} te kontrolki nie mają nazwy dla czytnika ekranu "
                + "ani widocznej etykiety:\n"
                + string.Join("\n", unnamed));
    }

    private static bool HasAutomationName(XElement element) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName.Equals(
                "AutomationProperties.Name",
                StringComparison.Ordinal)
            || attribute.Name.LocalName.Equals(
                "Name",
                StringComparison.Ordinal)
                && attribute.Name.Namespace == Presentation);

    /// <summary>
    /// True when the control, or anything inside it, carries literal text. A
    /// binding expression does not count: it may resolve to an empty string,
    /// and a control that sometimes announces nothing is the defect this test
    /// is about.
    /// </summary>
    private static bool HasVisibleLabel(XElement element) =>
        element.DescendantsAndSelf().Any(descendant => descendant
            .Attributes()
            .Any(attribute =>
                LabelAttributes.Contains(
                    attribute.Name.LocalName,
                    StringComparer.Ordinal)
                && attribute.Value.Length > 0
                && !attribute.Value.StartsWith('{')));

    private static string UiFilePath(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "GameShift.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Nie znaleziono katalogu repozytorium.");
        return Path.Combine(
            directory.FullName,
            "src",
            "GameShift.UI",
            fileName);
    }
}

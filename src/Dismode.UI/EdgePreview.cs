using System.Reflection; // PREVIEW-HACK
using System.Xml.Linq; // PREVIEW-HACK
using Dismode.Core.Domain.Identifiers; // PREVIEW-HACK
using Dismode.Core.History; // PREVIEW-HACK
using Dismode.Core.Profiles; // PREVIEW-HACK
using Dismode.UI.Services; // PREVIEW-HACK
using Microsoft.UI.Xaml; // PREVIEW-HACK
using Microsoft.UI.Xaml.Controls; // PREVIEW-HACK
using Microsoft.UI.Xaml.Media; // PREVIEW-HACK
using Microsoft.UI.Xaml.Media.Animation; // PREVIEW-HACK
namespace Dismode.UI; // PREVIEW-HACK
internal sealed class EdgePreviewActions : IEdgePanelActions // PREVIEW-HACK
{ // PREVIEW-HACK
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic; // PREVIEW-HACK
    private static EdgePanelWindow? s_panel; // PREVIEW-HACK
    private static readonly IReadOnlyList<(double AgeSeconds, double Fps)> s_sampleFps = // PREVIEW-HACK
    [ // PREVIEW-HACK
        (56, 118), (48, 126), (40, 134), (32, 146), (24, 152), (16, 148), (8, 139), (1, 144), // PREVIEW-HACK
    ]; // PREVIEW-HACK
    private static readonly SessionSummary s_sampleSession = new( // PREVIEW-HACK
        new SessionId(Guid.NewGuid()), // PREVIEW-HACK
        new GameProfileId(Guid.NewGuid()), // PREVIEW-HACK
        "7 Days to Die", // PREVIEW-HACK
        DateTimeOffset.UtcNow.AddMinutes(-85), // PREVIEW-HACK
        DateTimeOffset.UtcNow.AddMinutes(-5), // PREVIEW-HACK
        SessionCompletionStatus.Completed, // PREVIEW-HACK
        appliedActionCount: 4, // PREVIEW-HACK
        restoredActionCount: 4, // PREVIEW-HACK
        conflictCount: 0, // PREVIEW-HACK
        errorCount: 0, // PREVIEW-HACK
        durationSeconds: 4800); // PREVIEW-HACK
    public static Window Launch() // PREVIEW-HACK
    { // PREVIEW-HACK
        s_panel = new(new EdgePreviewActions()); // PREVIEW-HACK
        s_panel.SetHandleTransparency(int.TryParse(Environment.GetEnvironmentVariable("DISMODE_PREVIEW_TRANSPARENCY"), out int percent) ? percent : 50); // PREVIEW-HACK
        if (Environment.GetEnvironmentVariable("DISMODE_PREVIEW_OPEN") == "1") // PREVIEW-HACK
        { // PREVIEW-HACK
            typeof(EdgePanelWindow).GetMethod("OnHotkey", Flags)!.Invoke(s_panel, [0xD150]); // PREVIEW-HACK
            Thread.Sleep(350); // PREVIEW-HACK
            typeof(EdgePanelWindow).GetMethod("RefreshContent", Flags)!.Invoke(s_panel, null); // PREVIEW-HACK
            typeof(EdgePanelWindow).GetField("_slide", Flags)!.GetValue(s_panel).AsStoryboard()?.SkipToFill(); // PREVIEW-HACK
            typeof(EdgePanelWindow).GetField("_bars", Flags)!.GetValue(s_panel).AsStoryboard()?.SkipToFill(); // PREVIEW-HACK
            typeof(EdgePanelWindow).GetField("_selection", Flags)!.GetValue(s_panel).AsStoryboard()?.SkipToFill(); // PREVIEW-HACK
            if (Environment.GetEnvironmentVariable("DISMODE_PREVIEW_SCALE") == "100") // PREVIEW-HACK
            { // PREVIEW-HACK
                ((DispatcherTimer)typeof(EdgePanelWindow).GetField("_timer", Flags)!.GetValue(s_panel)!).Stop(); // PREVIEW-HACK
                FrameworkElement root = (FrameworkElement)s_panel.Content; // PREVIEW-HACK
                int h = (int)Math.Round(s_panel.AppWindow.Size.Height / 1.5); // PREVIEW-HACK
                root.Width = 450; // PREVIEW-HACK
                root.Height = s_panel.AppWindow.Size.Height; // PREVIEW-HACK
                root.HorizontalAlignment = HorizontalAlignment.Left; // PREVIEW-HACK
                root.VerticalAlignment = VerticalAlignment.Top; // PREVIEW-HACK
                root.RenderTransformOrigin = new(0, 0); // PREVIEW-HACK
                CompositeTransform transform = (CompositeTransform)root.RenderTransform; // PREVIEW-HACK
                transform.ScaleX = 2d / 3; // PREVIEW-HACK
                transform.ScaleY = 2d / 3; // PREVIEW-HACK
                s_panel.AppWindow.Resize(new(300, h)); // PREVIEW-HACK
            } // PREVIEW-HACK
        } // PREVIEW-HACK
        if (Environment.GetEnvironmentVariable("DISMODE_PREVIEW_SETTINGS") != "1") return s_panel; // PREVIEW-HACK
        XDocument doc = XDocument.Load(Path.Combine(Environment.CurrentDirectory, "src/Dismode.UI/MainWindow.xaml")); // PREVIEW-HACK
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml"; // PREVIEW-HACK
        XElement toggle = doc.Descendants().Single(e => e.Attribute(x + "Name")?.Value == "EdgePanelSettingsToggleSwitch"); // PREVIEW-HACK
        XElement row = doc.Descendants().Single(e => e.Attribute(x + "Name")?.Value == "EdgeHandleTransparencyRow"); // PREVIEW-HACK
        XElement container = new(toggle.Parent!.Name, toggle, row, new XElement(toggle.Parent.Name.Namespace + "TextBlock", new XAttribute("Text", "Alt+D"), new XAttribute("FontSize", "12"), new XAttribute("Opacity", "0.7"))); // PREVIEW-HACK
        container.SetAttributeValue("Spacing", "12"); // PREVIEW-HACK
        container.Descendants().Attributes("Toggled").Remove(); // PREVIEW-HACK
        container.Descendants().Attributes("ValueChanged").Remove(); // PREVIEW-HACK
        container.SetAttributeValue(XNamespace.Xmlns + "x", x.NamespaceName); // PREVIEW-HACK
        StackPanel ui = (StackPanel)Microsoft.UI.Xaml.Markup.XamlReader.Load(container.ToString()); // PREVIEW-HACK
        Slider slider = (Slider)ui.FindName("EdgeHandleTransparencySlider"); // PREVIEW-HACK
        TextBlock value = (TextBlock)ui.FindName("EdgeHandleTransparencyValue"); // PREVIEW-HACK
        slider.ValueChanged += (_, args) => { value.Text = $"{args.NewValue:0} %"; s_panel.SetHandleTransparency((int)args.NewValue); }; // PREVIEW-HACK
        Window settings = new() { Title = "Dismode — podgląd ustawień" }; // PREVIEW-HACK
        settings.Content = new Border { Padding = new(24), RequestedTheme = ElementTheme.Dark, Background = (Brush)Application.Current.Resources["DismodeCardBrush"], BorderBrush = (Brush)Application.Current.Resources["DismodeCardBorderBrush"], BorderThickness = new(1), CornerRadius = new(11), Child = ui }; // PREVIEW-HACK
        settings.AppWindow.Resize(new(540, 260)); // PREVIEW-HACK
        settings.Activate(); // PREVIEW-HACK
        return settings; // PREVIEW-HACK
    } // PREVIEW-HACK
    public EdgePanelContent ReadEdgePanelContent() // PREVIEW-HACK
    { // PREVIEW-HACK
        bool rich = Environment.GetEnvironmentVariable("DISMODE_PREVIEW_RICH") == "1"; // PREVIEW-HACK
        return new( // PREVIEW-HACK
            null, // PREVIEW-HACK
            null, // PREVIEW-HACK
            rich ? 144 : null, // PREVIEW-HACK
            false, // PREVIEW-HACK
            true, // PREVIEW-HACK
            "7 Days to Die", // PREVIEW-HACK
            rich, // PREVIEW-HACK
            PerformanceOverlayStyle.FullDeck, // PREVIEW-HACK
            false, // PREVIEW-HACK
            rich ? s_sampleSession : null, // PREVIEW-HACK
            rich ? s_sampleFps : [], // PREVIEW-HACK
            Environment.GetEnvironmentVariable("DISMODE_PREVIEW_LIGHT") == "1" ? ElementTheme.Light : ElementTheme.Dark); // PREVIEW-HACK
    } // PREVIEW-HACK
    public Task ToggleEdgePanelSessionAsync(XamlRoot root) => Task.CompletedTask; // PREVIEW-HACK
    public void SetEdgePanelOverlay(bool enabled) { } // PREVIEW-HACK
    public void SetEdgePanelOverlayStyle(PerformanceOverlayStyle style) { } // PREVIEW-HACK
} // PREVIEW-HACK
internal static class EdgePreviewExtension { internal static Storyboard? AsStoryboard(this object? value) => value as Storyboard; } // PREVIEW-HACK

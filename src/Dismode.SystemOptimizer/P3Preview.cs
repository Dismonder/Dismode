using System.Reflection; // PREVIEW-HACK
using Microsoft.UI.Xaml; // PREVIEW-HACK
using Microsoft.UI.Xaml.Controls; // PREVIEW-HACK
namespace Dismode.SystemOptimizer; // PREVIEW-HACK
internal static class P3Preview // PREVIEW-HACK
{ // PREVIEW-HACK
    private static Window? _window; // PREVIEW-HACK
    private static FileSystemWatcher? _watcher; // PREVIEW-HACK
    private static FrameworkElement? _root; // PREVIEW-HACK
    private static Viewbox? _viewbox; // PREVIEW-HACK
    internal static void Run() // PREVIEW-HACK
    { // PREVIEW-HACK
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromMinutes(25)); Environment.Exit(0); }); // PREVIEW-HACK
        _window = new MainWindow(); // PREVIEW-HACK
        _window.Activate(); // PREVIEW-HACK
         // PREVIEW-HACK
        _root = (FrameworkElement)_window.Content; // PREVIEW-HACK
        var path = Environment.GetEnvironmentVariable("DISMODE_PREVIEW_COMMAND")!; // PREVIEW-HACK
        _watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path)); // PREVIEW-HACK
        _watcher.Changed += async (_, _) => { await Task.Delay(150); _window.DispatcherQueue.TryEnqueue(() => Apply(path)); }; // PREVIEW-HACK
        _watcher.EnableRaisingEvents = true; // PREVIEW-HACK
        _root.Loaded += (_, _) => Apply(path); // PREVIEW-HACK
    } // PREVIEW-HACK
    private static void Apply(string path) // PREVIEW-HACK
    { // PREVIEW-HACK
        try // PREVIEW-HACK
        { // PREVIEW-HACK
            var parts = System.IO.File.ReadAllText(path).Split('|'); // PREVIEW-HACK
            var state = parts[0]; // PREVIEW-HACK
            if (state == "hide") { _window!.AppWindow.Hide(); return; } // PREVIEW-HACK
            if (_window!.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Restore(false); _window.AppWindow.Show(false); // PREVIEW-HACK
            if (_window is MainWindow main) // PREVIEW-HACK
            { // PREVIEW-HACK
                typeof(MainWindow).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [state == "compact" || state == "tray" || state == "tray-disable" ? "overview" : state]); // PREVIEW-HACK
                 // PREVIEW-HACK
            } // PREVIEW-HACK
            _root!.RequestedTheme = parts[2] == "light" ? ElementTheme.Light : ElementTheme.Dark; // PREVIEW-HACK
            if (_viewbox == null) // PREVIEW-HACK
            { // PREVIEW-HACK
                _window.Content = null; // PREVIEW-HACK
                _viewbox = new Viewbox { Child = _root, Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill }; // PREVIEW-HACK
                _window.Content = _viewbox; // PREVIEW-HACK
            } // PREVIEW-HACK
            var scale = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture); // PREVIEW-HACK
            var width = _window is MainWindow ? (state == "compact" ? 480 : 1200) : 400; // PREVIEW-HACK
            var height = _window is MainWindow ? (state == "compact" ? 360 : 820) : 548; // PREVIEW-HACK
            _root.Width = width; _root.Height = height; // PREVIEW-HACK
            var dpi = 1d; // PREVIEW-HACK
            _window.AppWindow.Resize(new((int)(width*scale*dpi), (int)(height*scale*dpi))); // PREVIEW-HACK
            _window.AppWindow.Move(new(24,24)); // PREVIEW-HACK
        } // PREVIEW-HACK
        catch (Exception exception) { System.IO.File.WriteAllText(path+".error",exception.ToString()); } // PREVIEW-HACK
    } // PREVIEW-HACK
} // PREVIEW-HACK

using System.Text.RegularExpressions;

namespace Dismode.UnitTests;

[TestClass]
public sealed class UiDefectRegressionTests
{
    [TestMethod]
    public void FailedTrayRegistrationCannotLeaveTheMainWindowHiddenWithoutAnIcon()
    {
        string source = Read("src/Dismode.UI/Services/TrayIconService.cs");
        int start = source.IndexOf("if (!ShellNotifyIcon(NotifyIconAdd", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        string registration = source[start..source.IndexOf("_iconData.TimeoutOrVersion", start, StringComparison.Ordinal)];
        Assert.IsTrue(registration.Contains("throw new Win32Exception(", StringComparison.Ordinal),
            "A failed Add and Modify must fail construction, not return an unusable tray object.");
        StringAssert.Contains(registration, "RemoveWindowSubclass(");
        StringAssert.Contains(registration, "DestroyIcon(_iconHandle)");
        Assert.IsTrue(Regex.IsMatch(Read("src/Dismode.UI/MainWindow.xaml.cs"),
            @"public void HideToTray\(\)\s*\{\s*if \(_trayIcon is null\)\s*\{\s*return;"),
            "Background startup must keep the window accessible when no tray icon exists.");
    }

    [TestMethod]
    public void QueuedHandleGeometryCannotTouchAClosedWindow()
    {
        string source = Read("src/Dismode.UI/EdgePanelHandleWindow.cs");
        StringAssert.Matches(source, new Regex(
            @"internal void Reposition\(\)\s*\{\s*if \(_disposed\)\s*\{\s*return;"));
        StringAssert.Matches(source, new Regex(
            @"public void Dispose\(\)\s*\{\s*if \(_disposed\)\s*\{\s*return;\s*\}\s*_disposed = true;"));
    }

    [TestMethod]
    public void OverlayClearsOldFramesBeforeChangingProcessIdentity()
    {
        string source = Read("src/Dismode.UI/PerformanceOverlayWindow.xaml.cs");
        int update = source.IndexOf("public void Update(\n        string gameName", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, update);
        string body = source[update..source.IndexOf("private sealed record OverlaySample", update, StringComparison.Ordinal)];
        Assert.IsTrue(Regex.IsMatch(body,
            @"if \(_targetProcessId != processId\)\s*\{[^}]*_frameTimeHistory.Clear\(\);[^}]*\}\s*_targetProcessId = processId;"),
            "A new PID must not inherit the previous process's frame history.");
    }

    [TestMethod]
    public void CancelledMemoryEndpointInitializationDisposesItsUnpublishedRuntime()
    {
        string source = Read("components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryServiceRuntime.cs");
        StringAssert.Matches(source, new Regex(
            @"catch \(OperationCanceledException\)\s*\{\s*await runtime.DisposeAsync\(\).ConfigureAwait\(false\);\s*throw;"));
    }

    [TestMethod]
    public void MainWindowProgrammaticBrushesFollowTheWindowTheme()
    {
        string source = Read("src/Dismode.UI/MainWindow.xaml.cs");
        Assert.IsFalse(Regex.IsMatch(source, @"\(Brush\)Application.Current.Resources\["),
            "Application resources do not follow an explicit theme on RootLayout.");
        StringAssert.Contains(source, "RootLayout.ActualThemeChanged += OnActualThemeChanged;");
        StringAssert.Contains(source, "RootLayout.ActualThemeChanged -= OnActualThemeChanged;");
        string theme = Read("src/Dismode.UI/MainWindow.Theme.cs");
        StringAssert.Contains(theme, "RootLayout.ActualTheme == ElementTheme.Light");
        StringAssert.Contains(theme, "brush.Color = ReadThemeBrush(key).Color;");
    }

    [TestMethod]
    public void ClosedSystemOptimizerDefersClientDisposalUntilOperationsFinishAndCannotRestartPolling()
    {
        string source = Read("src/Dismode.SystemOptimizer/MainWindow.xaml.cs");
        StringAssert.Matches(source, new Regex(
            @"private void MainWindow_Closed\([^)]*\)\s*\{\s*_closed = true;"));
        StringAssert.Matches(source, new Regex(
            @"private void UpdateRefreshActivity\(\)\s*\{\s*if \(_closed\)\s*\{\s*return;"));
        StringAssert.Matches(source, new Regex(
            @"private async Task RefreshAsync\(\)\s*\{\s*if \(_closed \|\|"));
        StringAssert.Matches(source, new Regex(
            @"private void UpdateOperationSurface\(\)\s*\{\s*if \(_closed\)\s*\{\s*if \(!_operationInProgress && !_refreshInProgress\)\s*\{\s*_client.Dispose\(\);\s*\}\s*return;"));
    }

    private static string Read(string path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, path)).ReplaceLineEndings("\n");
    }
}

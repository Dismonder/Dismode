using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Dismode.UnitTests;

[TestClass]
public sealed class UiResourceReferenceTests
{
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ExpectedMissingNestedResourceKeys =
        ["DismodeMissingBrush"];

    private static readonly string[] ExpectedOverlayModes =
    [
        "MinimalText|Tylko FPS",
        "CompactBar|FPS + ms",
        "FullDeck|Wszystkie szczegóły",
    ];

    private static readonly string[] ExpectedDashboardRows = ["Auto", "*"];

    private static readonly string[] ExpectedBannerTitleColumns = ["Auto", "*"];

    private static readonly string[] ExpectedLibraryActionHandlers =
    [
        "OnLaunchGameFromLibraryClicked",
        "OnOpenGameModeClicked",
        "OnManageOptiScalerClicked",
    ];

    private static readonly char[] DirectorySeparators =
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly Regex DefinedResourcePattern = new(
        "x:Key\\s*=\\s*[\"'](?<key>Dismode[A-Za-z0-9_.-]+)[\"']",
        RegexOptions.CultureInvariant);

    private static readonly Regex ReferencedResourcePattern = new(
        "\\{(?:ThemeResource|StaticResource)\\s+"
            + "(?:ResourceKey\\s*=\\s*)?"
            + "(?<key>Dismode[A-Za-z0-9_.-]+)"
            + "(?:\\s*,[^}]*)?\\s*\\}",
        RegexOptions.CultureInvariant);

    [TestMethod]
    public void EveryCustomUiResourceReferenceHasADefinition()
    {
        string repositoryRoot = FindRepositoryRoot();
        string uiDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "Dismode.UI");
        string[] missing = FindMissingResourceKeys(uiDirectory);

        Assert.IsEmpty(
            missing,
            "Brak definicji zasobów UI: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void PerformanceOverlayShowsTheThreeNumbersThatMatter()
    {
        // FPS mowi, jak jest srednio. 1% low mowi, jak jest w najgorszym
        // momencie — i to ten moment gracz odczuwa jako przyciecie. Czas
        // klatki daje jednostke, w ktorej da sie o tym mysliec.
        XDocument overlay = LoadUiXaml("PerformanceOverlayWindow.xaml");

        foreach (string name in new[]
        {
            "FpsValueText",
            "OnePercentLowText",
            "FrameTimeValueText",
            "FrameGraphLine",
        })
        {
            Assert.IsNotNull(
                FindNamedElement(overlay, name),
                $"Nakladka musi zawierac {name}.");
        }
    }

    [TestMethod]
    public void PerformanceOverlayUsesAMonospacedFaceForValues()
    {
        // Przy czcionce o zmiennej szerokosci liczba skacze w poziomie przy
        // kazdej zmianie wartosci i widac to katem oka w trakcie gry. Stala
        // szerokosc znaku jest tu funkcja, nie stylistyka.
        XDocument overlay = LoadUiXaml("PerformanceOverlayWindow.xaml");

        foreach (string name in new[]
        {
            "FpsValueText",
            "OnePercentLowText",
            "FrameTimeValueText",
        })
        {
            XElement element = FindNamedElement(overlay, name);
            Assert.AreEqual(
                "Consolas",
                (string?)element.Attribute("FontFamily"),
                $"{name} musi uzywac czcionki o stalej szerokosci.");
        }
    }

    [TestMethod]
    public void PerformanceOverlayHasOneLayoutAndNoAccentPalette()
    {
        // Trzy uklady i siedem nazwanych kolorow akcentu byly wyborem bez
        // znaczenia: nie zmienialy tego, co panel pokazuje, ani czy dziala.
        string settings = LoadUiSource("MainWindow.xaml");

        Assert.IsFalse(
            settings.Contains("OverlayThemeSelector", StringComparison.Ordinal),
            "Paleta kolorow akcentu powinna byc usunieta.");
        Assert.IsFalse(
            settings.Contains("OverlayStyleSelector", StringComparison.Ordinal),
            "Wybor ukladu nakladki powinien byc usuniety.");

        XDocument overlay = LoadUiXaml("PerformanceOverlayWindow.xaml");
        foreach (string gone in new[]
        {
            "CompactBarLayout",
            "MinimalTextLayout",
            "CompactFpsValueText",
        })
        {
            Assert.IsNull(
                overlay.Descendants().FirstOrDefault(element =>
                    (string?)element.Attribute(XamlNamespace + "Name") == gone),
                $"Element {gone} nalezy do usunietych ukladow.");
        }
    }

    [TestMethod]
    public void PerformanceOverlayDrawsWithoutASessionAsWell()
    {
        // Gra uruchomiona poza Dismodeem to normalny przypadek. Nakladka
        // ograniczona do telemetrii sesji pokazywala wtedy myslniki, co czyta
        // sie jako zepsute — i bylo zepsute.
        string overlaySource = LoadUiSource("PerformanceOverlayWindow.xaml.cs");
        string windowSource = LoadUiSource("MainWindow.xaml.cs");

        StringAssert.Contains(
            overlaySource,
            "public void Update(\n        string gameName,".Replace("\n", "\n"));
        StringAssert.Contains(windowSource, "ForegroundGameWatcher");
        StringAssert.Contains(windowSource, "OnOverlayPollTick");
    }

    [TestMethod]
    public void FpsTrackingAndOverlayHaveIndependentSettings()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement tracking = FindNamedElement(
            mainWindow,
            "FpsTrackingToggleSwitch");
        XElement overlay = FindNamedElement(
            mainWindow,
            "FpsOverlayToggleSwitch");
        XElement settingsTracking = FindNamedElement(
            mainWindow,
            "FpsTrackingSettingsToggleSwitch");
        XElement settingsOverlay = FindNamedElement(
            mainWindow,
            "FpsOverlaySettingsToggleSwitch");

        Assert.AreEqual("True", (string?)tracking.Attribute("IsOn"));
        Assert.AreEqual("True", (string?)overlay.Attribute("IsOn"));
        Assert.AreEqual("True", (string?)settingsTracking.Attribute("IsOn"));
        Assert.AreEqual("True", (string?)settingsOverlay.Attribute("IsOn"));
        Assert.AreNotEqual(
            (string?)tracking.Attribute(XamlNamespace + "Name"),
            (string?)overlay.Attribute(XamlNamespace + "Name"));
        Assert.AreNotEqual(
            (string?)settingsTracking.Attribute(XamlNamespace + "Name"),
            (string?)settingsOverlay.Attribute(XamlNamespace + "Name"));

        string codeBehind = LoadUiSource("MainWindow.xaml.cs");
        StringAssert.Contains(codeBehind, "IsFpsTrackingEnabled");
        StringAssert.Contains(codeBehind, "SetFrameRateTrackingAsync");
        StringAssert.Contains(codeBehind, "FpsTrackingSettingsToggleSwitch");
        StringAssert.Contains(codeBehind, "FpsOverlaySettingsToggleSwitch");
    }

    [TestMethod]
    public void CpuSectionStatesWhatTheMachineCanDoAndDefaultsToOn()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement topology = FindNamedElement(mainWindow, "CpuTopologyText");
        XElement toggle = FindNamedElement(mainWindow, "ProBalanceToggleSwitch");

        // Od 2026-09-11 domyslnie wlaczone, na zyczenie wlasciciela produktu:
        // to jedyny mechanizm z udowodnionym zyskiem (p99 lepsze o 60,7% pod
        // obciazeniem), a stan przelacznika nie jest zapisywany, wiec
        // „wylaczony" znaczylo „wylaczony przy kazdym starcie". Przelacznik
        // zostaje widoczny w ustawieniach, zeby dalo sie go wylaczyc.
        Assert.AreEqual("True", (string?)toggle.Attribute("IsOn"));

        // Opis topologii jest wypelniany odczytem z maszyny; wartosc w XAML to
        // tylko stan przejsciowy do czasu odczytu.
        Assert.IsNotNull((string?)topology.Attribute("Text"));

        string codeBehind = LoadUiSource("MainWindow.xaml.cs");
        StringAssert.Contains(codeBehind, "SystemCpuTopologyProvider.Read()");
        StringAssert.Contains(codeBehind, "ApplyCpuTopologyDescription");
        StringAssert.Contains(codeBehind, "OnProBalanceSettingChanged");

        // Przelacznik ma faktycznie dojechac do sesji. Sam opis pod nim to
        // usterka gorsza niz brak opcji: wyglada na dzialajaca i nie dziala.
        StringAssert.Contains(
            codeBehind,
            "enableProBalance: ProBalanceToggleSwitch.IsOn");

        // Odczyt topologii dotyka rejestru i CPU sets, wiec nie moze blokowac
        // watku interfejsu.
        StringAssert.Contains(codeBehind, "await Task.Run(");
    }

    [TestMethod]
    public void DiagnosticsExposesSeparateSystemOptimizerCard()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement card = FindNamedElement(mainWindow, "SystemOptimizerCard");

        // Karta ma byc osobna i na pelna szerokosc; konkretny numer wiersza
        // jest szczegolem ukladu i zmienia sie przy przebudowie ustawien.
        Assert.AreEqual("2", (string?)card.Attribute("Grid.ColumnSpan"));
        Assert.IsNotNull(
            card.Attribute("Grid.Row"),
            "Karta musi byc jawnie umieszczona w wierszu siatki ustawien.");
        Assert.IsTrue(ContainsNamedElement(card, "SystemOptimizerStatusText"));
        Assert.IsTrue(ContainsNamedElement(card, "SystemOptimizerProfileText"));
        XElement button = FindNamedElement(card, "OpenSystemOptimizerButton");
        Assert.AreEqual(
            "OnOpenSystemOptimizerClicked",
            (string?)button.Attribute("Click"));
    }

    [TestMethod]
    public void DashboardNotificationsUseDedicatedAutoRowOutsideCanvas()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement dashboard = FindNamedElement(mainWindow, "DashboardPage");
        XElement notificationRow =
            FindNamedElement(dashboard, "DashboardNotificationRow");
        XElement content =
            FindNamedElement(dashboard, "DashboardContentViewbox");

        string[] rowHeights = dashboard
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Select(element => (string?)element.Attribute("Height") ?? "*")
            .ToArray();
        CollectionAssert.AreEqual(ExpectedDashboardRows, rowHeights);
        Assert.AreEqual("0", (string?)content.Attribute("Grid.Row"));
        Assert.AreEqual("2", (string?)content.Attribute("Grid.RowSpan"));
        Assert.AreEqual("100", (string?)notificationRow.Attribute("Canvas.ZIndex"));
        Assert.IsNull(notificationRow.Attribute("RowSpacing"));

        foreach (string name in new[]
                 {
                     "DashboardInfoBar",
                     "UnoptimizedGameBanner",
                 })
        {
            XElement notification = FindNamedElement(dashboard, name);
            Assert.IsTrue(notification.Ancestors().Contains(notificationRow));
            Assert.IsFalse(notification.Ancestors().Any(
                element => element.Name.LocalName == "Canvas"));
        }

        Assert.AreEqual(
            "0,8,0,0",
            (string?)FindNamedElement(
                notificationRow,
                "UnoptimizedGameBanner").Attribute("Margin"));
    }

    [TestMethod]
    public void GlobalProgressReservesOverlaySpaceInsteadOfChangingLayout()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement progress = FindNamedElement(mainWindow, "GlobalProgress");

        Assert.AreEqual("Visible", (string?)progress.Attribute("Visibility"));
        Assert.AreEqual("0", (string?)progress.Attribute("Opacity"));
        Assert.AreEqual("False", (string?)progress.Attribute("IsHitTestVisible"));
        Assert.AreEqual("10", (string?)progress.Attribute("Canvas.ZIndex"));

        string codeBehind = LoadUiSource("MainWindow.xaml.cs");
        StringAssert.Contains(codeBehind, "GlobalProgress.Opacity = isBusy ? 1 : 0;");
        Assert.IsFalse(codeBehind.Contains(
            "GlobalProgress.Visibility",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnoptimizedGameBannerConstrainsNameAndStacksActionWhenCompact()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement banner =
            FindNamedElement(mainWindow, "UnoptimizedGameBanner");
        XElement titleLayout =
            FindNamedElement(banner, "UnoptimizedGameTitleLayout");
        XElement gameName =
            FindNamedElement(titleLayout, "UnoptimizedGameNameText");
        XElement actions =
            FindNamedElement(banner, "UnoptimizedGameActions");
        XElement compactState =
            FindNamedElement(mainWindow, "CompactWindowLayout");

        string[] titleColumns = titleLayout
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => (string?)element.Attribute("Width") ?? "*")
            .ToArray();
        CollectionAssert.AreEqual(ExpectedBannerTitleColumns, titleColumns);
        Assert.AreEqual("1", (string?)gameName.Attribute("Grid.Column"));
        Assert.AreEqual("Wrap", (string?)gameName.Attribute("TextWrapping"));
        Assert.AreEqual("2", (string?)actions.Attribute("Grid.Column"));
        Assert.AreEqual(
            "1",
            FindVisualStateSetterValue(
                compactState,
                "UnoptimizedGameActions.(Grid.Row)"));
        Assert.AreEqual(
            "1",
            FindVisualStateSetterValue(
                compactState,
                "UnoptimizedGameActions.(Grid.Column)"));
    }

    [TestMethod]
    public void DashboardFpsSeparatesNoDataFromTelemetryAndTogglesBoth()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement noData =
            FindNamedElement(mainWindow, "DashboardFpsNoDataState");
        XElement telemetry =
            FindNamedElement(mainWindow, "DashboardFpsTelemetryContent");
        string codeBehind = LoadUiSource("MainWindow.xaml.cs");

        Assert.AreEqual("Visible", (string?)noData.Attribute("Visibility"));
        Assert.AreEqual(
            "Collapsed",
            (string?)telemetry.Attribute("Visibility"));
        StringAssert.Contains(
            codeBehind,
            "private void SetDashboardTelemetryAvailability(bool hasValidSample)");
        StringAssert.Contains(
            codeBehind,
            "DashboardFpsNoDataState.Visibility = hasValidSample");
        StringAssert.Contains(
            codeBehind,
            "DashboardFpsTelemetryContent.Visibility = hasValidSample");
        StringAssert.Contains(
            codeBehind,
            "SetDashboardTelemetryAvailability(hasValidSample: true);");
        StringAssert.Contains(
            codeBehind,
            "SetDashboardTelemetryAvailability(hasValidSample: false);");
    }

    [TestMethod]
    public void LibraryUsesResponsivePosterGridAndCompactContainers()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement page = FindNamedElement(mainWindow, "ProfilesPage");
        XElement list = FindNamedElement(page, "ProfilesList");
        XElement wrapGrid = list
            .Descendants()
            .Single(element => element.Name.LocalName == "ItemsWrapGrid");
        XElement containerStyle = list
            .Descendants()
            .Single(element => element.Name.LocalName == "Style"
                && string.Equals(
                    (string?)element.Attribute("TargetType"),
                    "ListViewItem",
                    StringComparison.Ordinal));
        XElement compactButtonStyle = page
            .Descendants()
            .Single(element => element.Name.LocalName == "Style"
                && string.Equals(
                    (string?)element.Attribute(XamlNamespace + "Key"),
                    "DismodeLibraryCompactButtonStyle",
                    StringComparison.Ordinal));

        Assert.AreEqual("2560", (string?)page.Attribute("MaxWidth"));
        Assert.AreEqual("280", (string?)wrapGrid.Attribute("ItemWidth"));
        Assert.AreEqual("0", FindSetterValue(containerStyle, "Padding"));
        Assert.AreEqual("0", FindSetterValue(containerStyle, "Margin"));
        Assert.AreEqual("Button", (string?)compactButtonStyle.Attribute("TargetType"));
        Assert.IsNull(compactButtonStyle.Attribute("BasedOn"));
        Assert.AreEqual("0", FindSetterValue(compactButtonStyle, "MinHeight"));
        // Lista ma teraz dwa szablony: naglowek polki i element. Test dotyczy
        // elementu, wiec celuje w ListView.ItemTemplate zamiast zakladac,
        // ze szablon jest tylko jeden.
        XElement itemTemplate = list
            .Descendants()
            .Single(element => element.Name.LocalName == "ListView.ItemTemplate")
            .Elements()
            .Single(element => element.Name.LocalName == "DataTemplate");
        XElement profileState =
            FindNamedElement(itemTemplate, "LibraryProfileStateText");
        XElement gameInfo =
            FindNamedElement(itemTemplate, "LibraryGameInfo");
        Assert.AreEqual(
            "{Binding StateLabel}",
            (string?)profileState.Attribute("Text"));
        Assert.AreEqual(
            "Wrap",
            (string?)profileState.Attribute("TextWrapping"));
        Assert.IsFalse(gameInfo
            .Descendants()
            .Any(element => element.Name.LocalName == "Ellipse"));
        Assert.HasCount(
            3,
            list.Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .Where(element => string.Equals(
                    (string?)element.Attribute("Style"),
                    "{StaticResource DismodeLibraryCompactButtonStyle}",
                    StringComparison.Ordinal)));
        CollectionAssert.AreEqual(
            ExpectedLibraryActionHandlers,
            itemTemplate
                .Descendants()
                .Where(element => element.Name.LocalName == "Button")
                .Select(element => (string?)element.Attribute("Click"))
                .Where(handler => handler is not null)
                .Select(handler => handler!)
                .ToArray());
    }

    [TestMethod]
    public void PosterAndHeroArtworkAreUsedByTheirIntendedSurfaces()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement railPoster =
            FindNamedElement(mainWindow, "DashboardRailPosterImage");
        XElement libraryPoster =
            FindNamedElement(mainWindow, "LibraryPosterImage");
        string codeBehind = LoadUiSource("MainWindow.xaml.cs");

        Assert.AreEqual(
            "{Binding TileArtworkSource}",
            (string?)railPoster.Attribute("Source"));
        Assert.AreEqual(
            "{Binding LibraryArtworkSource}",
            (string?)libraryPoster.Attribute("Source"));
        Assert.AreEqual("{Binding TileArtworkStretch}", (string?)railPoster.Attribute("Stretch"));
        Assert.AreEqual("{Binding LibraryArtworkStretch}", (string?)libraryPoster.Attribute("Stretch"));
        StringAssert.Contains(
            codeBehind,
            "item.PosterArtworkSource = await LocalArtworkImageLoader.LoadAsync(");
        StringAssert.Contains(
            codeBehind,
            "item.HeroArtworkSource = await LocalArtworkImageLoader.LoadAsync(");
        StringAssert.Contains(codeBehind, "profile?.HeroArtworkSource");
        StringAssert.Contains(codeBehind, "?? profile?.PosterArtworkSource");
    }

    [TestMethod]
    public void MainLayoutStacksWideSectionsBelow1320()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        Assert.IsTrue(mainWindow
            .Descendants()
            .Any(element => element.Name.LocalName == "AdaptiveTrigger"
                && string.Equals(
                    (string?)element.Attribute("MinWindowWidth"),
                    "1320",
                    StringComparison.Ordinal)));

        HashSet<string> setterTargets = mainWindow
            .Descendants()
            .Where(element => element.Name.LocalName == "Setter")
            .Select(element => (string?)element.Attribute("Target"))
            .Where(target => target is not null)
            .Select(target => target!)
            .ToHashSet(StringComparer.Ordinal);
        string[] requiredTargets =
        [
            "ProfilesHeaderActions.(Grid.Row)",
            "PlanSecondarySection.(Grid.Row)",
            "HistoryHeaderAction.(Grid.Row)",
            "CheckForUpdatesButton.(Grid.Row)",
            "UpdatesSettingsCard.(Grid.Row)",
            "DiagnosticsStatusCard.(Grid.Row)",
        ];
        foreach (string target in requiredTargets)
        {
            Assert.Contains(target, setterTargets);
        }
    }

    [TestMethod]
    public void HistoryAndDiagnosticsAllowLongLocalizedTextToWrap()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        string[] wrappingTextNames =
        [
            "HistoryGameNameText",
            "HistoryStatusText",
            "HistoryActionSummaryText",
            "HistoryEndedAtText",
            "DiagnosticsGamingEnvironmentText",
            "DiagnosticsGamingRecommendationsText",
            "DiagnosticsSessionHostText",
            "DiagnosticsSystemAgentText",
        ];

        foreach (string name in wrappingTextNames)
        {
            XElement text = FindNamedElement(mainWindow, name);
            Assert.IsNull(text.Attribute("Width"));
            Assert.IsNull(text.Attribute("MaxWidth"));
            Assert.AreEqual(
                "Wrap",
                (string?)text.Attribute("TextWrapping"),
                $"Element {name} must wrap instead of forcing clipping.");
        }

        Assert.AreEqual(
            "Top",
            (string?)FindNamedElement(
                mainWindow,
                "DiagnosticsGamingCard").Attribute("VerticalAlignment"));

        XElement historyIdentity =
            FindNamedElement(mainWindow, "HistoryIdentityLayout");
        XElement historyStatus =
            FindNamedElement(historyIdentity, "HistoryStatusText");
        Assert.AreSame(historyIdentity, historyStatus.Parent);
        Assert.AreEqual("1", (string?)historyStatus.Attribute("Grid.Row"));
        Assert.IsFalse(historyIdentity
            .Elements()
            .Any(element => element.Name.LocalName == "Grid.ColumnDefinitions"));
    }

    [TestMethod]
    public void SystemImpactReservesMoreWidthForLongMetrics()
    {
        XDocument mainWindow = LoadUiXaml("MainWindow.xaml");
        XElement metrics =
            FindNamedElement(mainWindow, "DashboardSystemImpactMetrics");
        double[] widths = metrics
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Select(element => ParseStarWidth(
                (string?)element.Attribute("Width") ?? "1*"))
            .ToArray();

        Assert.HasCount(5, widths);
        Assert.IsGreaterThan(widths[0], widths[2]);
        Assert.IsGreaterThan(widths[2], widths[3]);
        Assert.IsGreaterThan(widths[3], widths[4]);
        foreach (string name in new[]
                 {
                     "DashboardCpuText",
                     "DashboardGpuText",
                     "DashboardMemoryText",
                     "DashboardDiskText",
                     "DashboardRecoveredMemoryText",
                 })
        {
            Assert.AreEqual(
                "Wrap",
                (string?)FindNamedElement(mainWindow, name)
                    .Attribute("TextWrapping"));
        }
    }

    [TestMethod]
    public void ProductNameComesFromProductInformationInsteadOfOldVersionLiteral()
    {
        string xaml = LoadUiSource("MainWindow.xaml");
        string codeBehind = LoadUiSource("MainWindow.xaml.cs");

        Assert.IsFalse(Regex.IsMatch(
            xaml,
            @"Dismode\s+\d+\.\d+\.\d+",
            RegexOptions.CultureInvariant));
        StringAssert.Contains(
            codeBehind,
            "UpdatesCurrentVersionText.Text =\n            ProductInformation.FullDisplayName;");
        StringAssert.Contains(
            codeBehind,
            "SettingsVersionText.Text = ProductInformation.FullDisplayName;");
    }

    [TestMethod]
    public void OptiScalerDialogExposesStableBetaAndNightlyVersions()
    {
        string codeBehind = LoadUiSource("MainWindow.xaml.cs");

        StringAssert.Contains(codeBehind, "Header = \"Kanał wersji\"");
        StringAssert.Contains(codeBehind, "Header = \"Wersja\"");
        StringAssert.Contains(codeBehind, "Stabilny — zalecany");
        StringAssert.Contains(codeBehind, "Beta — społecznościowy");
        StringAssert.Contains(codeBehind, "Nightly — oficjalny codzienny");
        StringAssert.Contains(codeBehind, "GetAvailableVersionsAsync");
        StringAssert.Contains(codeBehind, "ExperimentalUseConfirmed");
    }

    [TestMethod]
    public void OverlayOpacityTargetsStatisticsContentAndKeepsWindowFlags()
    {
        string codeBehind = LoadUiSource("PerformanceOverlayWindow.xaml.cs");
        int methodStart = codeBehind.IndexOf(
            "private void ApplyWindowOpacity(int opacityPercent)",
            StringComparison.Ordinal);
        int methodEnd = codeBehind.IndexOf(
            "public void SetRequestedVisibility",
            methodStart,
            StringComparison.Ordinal);
        string opacityMethod = codeBehind[methodStart..methodEnd];

        StringAssert.Contains(
            opacityMethod,
            "OverlayStatisticsContent.Opacity = opacity;");
        Assert.IsFalse(opacityMethod.Contains(
            "OverlayRoot.Opacity",
            StringComparison.Ordinal));
        Assert.IsFalse(opacityMethod.Contains(
            "SetLayeredWindowAttributes",
            StringComparison.Ordinal));
        StringAssert.Contains(codeBehind, "WsExLayered");
        StringAssert.Contains(codeBehind, "WsExTransparent");
        StringAssert.Contains(codeBehind, "WsExNoActivate");
        StringAssert.Contains(
            codeBehind,
            "private const uint LwaColorKey = 0x00000001;");
        StringAssert.Contains(
            codeBehind,
            "private const uint TransparentColorKey = 0x00000000;");
        StringAssert.Contains(
            codeBehind,
            "SetLayeredWindowAttributes(\n                _windowHandle,\n                TransparentColorKey,\n                byte.MaxValue,\n                LwaColorKey)");
        StringAssert.Contains(
            codeBehind,
            "presenter.SetBorderAndTitleBar(\n                hasBorder: false,\n                hasTitleBar: false);");
        Assert.IsFalse(codeBehind.Contains(
            "SystemBackdrop =",
            StringComparison.Ordinal));
        Assert.IsFalse(codeBehind.Contains(
            "DwmExtendFrameIntoClientArea",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void NestedResourceDictionariesAndViewsAreValidated()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"Dismode-UiResources-{Guid.NewGuid():N}");

        try
        {
            string themesDirectory = Path.Combine(
                temporaryDirectory,
                "Themes");
            string viewsDirectory = Path.Combine(
                temporaryDirectory,
                "Views");
            Directory.CreateDirectory(themesDirectory);
            Directory.CreateDirectory(viewsDirectory);
            File.WriteAllText(
                Path.Combine(themesDirectory, "Colors.xaml"),
                "<ResourceDictionary x:Key=\"DismodeNestedBrush\" />");
            File.WriteAllText(
                Path.Combine(viewsDirectory, "Dashboard.xaml"),
                "<Grid Background=\"{ThemeResource DismodeNestedBrush}\" "
                    + "BorderBrush=\"{StaticResource DismodeMissingBrush}\" />");

            string[] missing = FindMissingResourceKeys(temporaryDirectory);

            CollectionAssert.AreEqual(
                ExpectedMissingNestedResourceKeys,
                missing);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UiPublishTargetDiscoversCompiledXamlRecursively()
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "Dismode.UI",
            "Dismode.UI.csproj");
        XDocument project = XDocument.Load(projectPath);
        XElement target = project
            .Descendants("Target")
            .Single(element => string.Equals(
                (string?)element.Attribute("Name"),
                "CopyLooseWinUiResourcesToPublish",
                StringComparison.Ordinal));
        XElement xbfItem = target.Descendants("_LooseWinUiXbf").Single();
        XElement priItem = target
            .Descendants("_LooseWinUiResource")
            .Single(element => ((string?)element.Attribute("Include"))
                ?.EndsWith(".pri", StringComparison.Ordinal) == true);
        XElement copy = target.Descendants("Copy").Single();

        Assert.AreEqual(
            "$(TargetDir)**\\*.xbf",
            (string?)xbfItem.Attribute("Include"));
        StringAssert.Contains(
            (string?)xbfItem.Attribute("Exclude") ?? string.Empty,
            "$(PublishDir)**\\*.xbf");
        Assert.AreEqual(
            "$(TargetDir)$(TargetName).pri",
            (string?)priItem.Attribute("Include"));
        StringAssert.Contains(
            (string?)copy.Attribute("DestinationFiles") ?? string.Empty,
            "%(RecursiveDir)");
    }

    private static string[] FindMissingResourceKeys(string uiDirectory)
    {
        string[] xamlFiles = EnumerateSourceXamlFiles(uiDirectory).ToArray();
        HashSet<string> definitions = xamlFiles
            .SelectMany(path => DefinedResourcePattern
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .ToHashSet(StringComparer.Ordinal);

        return xamlFiles
            .SelectMany(path => ReferencedResourcePattern
                .Matches(File.ReadAllText(path))
                .Select(match => match.Groups["key"].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(key => !definitions.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string LoadUiSource(string fileName)
    {
        string repositoryRoot = FindRepositoryRoot();
        // Wzorce w testach maja LF; checkout z core.autocrlf=true daje CRLF.
        return File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "Dismode.UI",
                fileName))
            .ReplaceLineEndings("\n");
    }

    private static string? FindSetterValue(
        XElement style,
        string propertyName) =>
        style
            .Elements()
            .Single(element => element.Name.LocalName == "Setter"
                && string.Equals(
                    (string?)element.Attribute("Property"),
                    propertyName,
                    StringComparison.Ordinal))
            .Attribute("Value")
            ?.Value;

    private static string? FindVisualStateSetterValue(
        XElement visualState,
        string target) =>
        visualState
            .Descendants()
            .Single(element => element.Name.LocalName == "Setter"
                && string.Equals(
                    (string?)element.Attribute("Target"),
                    target,
                    StringComparison.Ordinal))
            .Attribute("Value")
            ?.Value;

    private static double ParseStarWidth(string value)
    {
        string multiplier = value.TrimEnd('*');
        return string.IsNullOrEmpty(multiplier)
            ? 1d
            : double.Parse(
                multiplier,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static XDocument LoadUiXaml(string fileName)
    {
        string repositoryRoot = FindRepositoryRoot();
        return XDocument.Load(
            Path.Combine(
                repositoryRoot,
                "src",
                "Dismode.UI",
                fileName));
    }

    private static XElement FindNamedElement(
        XContainer container,
        string name) =>
        container
            .Descendants()
            .Single(element => string.Equals(
                (string?)element.Attribute(XamlNamespace + "Name"),
                name,
                StringComparison.Ordinal));

    private static bool ContainsNamedElement(
        XContainer container,
        string name) =>
        container
            .Descendants()
            .Any(element => string.Equals(
                (string?)element.Attribute(XamlNamespace + "Name"),
                name,
                StringComparison.Ordinal));

    private static IEnumerable<string> EnumerateSourceXamlFiles(
        string uiDirectory)
    {
        return Directory
            .EnumerateFiles(uiDirectory, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(uiDirectory, path));
    }

    private static bool IsBuildOutputPath(
        string uiDirectory,
        string path)
    {
        string relativePath = Path.GetRelativePath(uiDirectory, path);
        string[] segments = relativePath.Split(
            DirectorySeparators,
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Dismode.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                "Nie znaleziono katalogu repozytorium Dismode.");
    }
}

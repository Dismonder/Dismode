# Rekonstrukcje interfejsu Dismode 0.8.0

Czyste, samodzielne SVG bez skryptów, `foreignObject`, zewnętrznych obrazów i czcionek. Każdy plik ma polski `title` i `desc`. Wszystkie widoki mają planszę 1600×900; karta społecznościowa ma 1280×640, a hero README 1600×600. Font to `Segoe UI Variable, Segoe UI, sans-serif`.

| Plik | Źródło i odwzorowane elementy |
|---|---|
| `overview.svg` | `src/Dismode.UI/MainWindow.xaml`: `DashboardBentoGrid` 1444×944, hero 1273×359, boczne kafelki 157×70, karty sesji, pomiaru, zasobów i recovery; skalowanie jednolite jak w `DashboardContentViewbox`. |
| `library.svg` | `src/Dismode.UI/MainWindow.xaml`: `ProfilesPage`, `LibraryHeroPanel` (208 px wysokości), nagłówek, wyszukiwanie, postery 264×382 w siatce 280×398. Hero 1273×359 i szerokie kafelki 70 px są w przeglądzie, nie w `ProfilesPage`. |
| `session.svg` | `src/Dismode.UI/MainWindow.xaml`: `PlanPage`, kolumny 1:1,15, profil i priorytet, HUD, reguły aplikacji i wykonanie planu; pasek czterech faz ilustruje cykl opisany w `docs/CODEX_CONTEXT.md`. |
| `history.svg` | `src/Dismode.UI/MainWindow.xaml`: `HistoryPage`, cztery metryki oraz `HistoryList` z nazwą, statusem, pomiarem, akcjami, datą i czasem. |
| `diagnostics.svg` | `src/Dismode.UI/MainWindow.xaml`: `DiagnosticsPage`, konfiguracja Windows/GPU, status hosta i agenta, motyw, efekty, FPS, procesor i automat; dalsze karty znajdują się poniżej viewportu. |
| `overlay.svg` | `src/Dismode.UI/PerformanceOverlayWindow.xaml`: same liczby z przesuniętym cieniem, bez panelu; grupa `hud` zajmuje pole 344×152 na neutralnej scenie 1600×900. Wykres w obecnym XAML jest `Collapsed`. Segoe UI zastępuje źródłowe Consolas zgodnie z wymaganiem tych grafik. |
| `system-optimizer.svg` | `src/Dismode.SystemOptimizer/MainWindow.xaml` i `App.xaml`: pasek 48 px, nawigacja 244 px, `OverviewPage`, trzy metryki i opis ochrony; stan publicznego klienta tylko do odczytu. |
| `memory-optimizer.svg` | `components/Dismode.MemoryOptimizer/src/Dismode.MemoryOptimizer/TrayMenuWindow.xaml` i `App.xaml`: panel 400×548 w skali 1:1 na planszy 1600×900, padding 20 px, karta RAM 130 px i przyciski 44 px; nie jest to pełne `MainWindow.xaml`. |
| `social-preview.svg` | Uproszczone logo D oraz fragment `DashboardBentoGrid` z `src/Dismode.UI/MainWindow.xaml`; karta GitHub 1280×640. |
| `readme-hero.svg` | Uproszczone logo D i miniatura `DashboardBentoGrid` z `src/Dismode.UI/MainWindow.xaml`; baner 1600×600. |

Wspólne powierzchnie, cyjan `#00D7E2`, gradienty, tekst, obramowania i promienie odtworzono z ciemnych zasobów `src/Dismode.UI/App.xaml`, w tym `DismodeBentoCardStyle` (11 px), `DismodeDeckHeaderStyle` (11 px), `DismodePageTitleStyle` (32 px) oraz `DismodeEyebrowStyle` (12 px). Mica jest zastąpiona gradientem. Okładki i scena gry są neutralnymi rysunkami SVG; logo `assets/branding/Dismode.png` ma 682 314 B, więc zastosowano uproszczoną literę D z niebiesko-cyjanowym gradientem i smugami.

Gry, sprzęt, daty, RAM i pomiary są realistycznymi danymi ilustracyjnymi. Nie dokumentują wzrostu FPS. Kanały niedostępne w przeglądzie pozostają oznaczone `—`. Ilustracje są rekonstrukcjami źródeł, nie zrzutami działającej aplikacji; pikselowa zgodność z rasteryzacją WinUI wymaga osobnego porównania w Windows.

Z katalogu głównego repozytorium:

```powershell
node tools/render-brand.mjs
```

Otwórz wygenerowany `assets/brand/ui/index.html` w przeglądarce. Obrazy nie mają `max-width` i zachowują natywną skalę. Dla PNG otwórz link do osobnego SVG, ustaw viewport zgodny z jego wymiarami i zrób zrzut przy skali przeglądarki 100% oraz DPR 1. Skrypt nie instaluje pakietów ani nie rasteruje obrazów. Witryna przechowuje potrzebne, identyczne kopie w `infrastructure/website/public/assets/ui/`.

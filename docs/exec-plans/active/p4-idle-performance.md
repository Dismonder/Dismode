# P4: praca w tle

Zakres obejmuje caly patch P4, lacznie z zachowana praca z commita WIP
`3fa9c1b` i `60055dd`. Nie zmieniano EdgePanel, ustawien, okien ani tray UI optymalizatorow.
Nie zmieniano tekstow interfejsu ani schematu danych.

## Zmiany

- Widocznosc i minimalizacja steruja odswiezaniem okna oraz oboma ProgressRing.
  Powrot natychmiast odswieza stan. Metryki komputera sa probkowane tylko na
  widocznym pulpicie; wykres nie jest rysowany w tle.
- Oddzielny licznik podtrzymuje obowiazki sesji/automatu w zasobniku. Zapasowe
  pytanie o sesje pozostaje konieczne: inny klient moze uruchomic sesje po
  odpowiedzi hosta "brak sesji". Zegar monotoniczny ogranicza pytania bez sesji
  do jednego na 15 s. Aktywna sesja i odsloniecie okna wymuszaja aktualny odczyt.
- Nakladka zachowuje ostatnia probke, ale renderuje i probkuje sprzet tylko
  wtedy, gdy jest widoczna. Zdarzenie zmiany pierwszego planu wznawia jej prace.
  Nieudane przechwytywanie jest odlaczane, zanim oczekiwanie przejmie zdarzenie.
- SessionHost juz uruchamial monitor i ProBalance tylko na czas sesji gry.
  Nie wprowadzano dodatkowej petli ani zmian w jego obsludze sesji.
- SystemAgent usypia sprawdzanie timeoutu recovery bez oczekujacego eksperymentu.
  Zmiana fazy budzi sprawdzanie; podczas eksperymentu pozostaje rytm 60 s.
  Rzadki odczyt kontrolny nie skanuje calego dziennika recovery.
- MemoryService uzywa powiadomien WTS do wykrywania zmian sesji Windows oraz
  sygnalu po zapisie/pauzie/wznowieniu ustawien. Przy wylaczonym automacie lub
  cooldownie nie pobiera metryk RAM/CPU. Aktywny automat zachowuje ustawiony rytm.

## Metoda pomiaru

Zainstalowane procesy probkowano przez co najmniej 120 s, bez zatrzymywania,
restartowania ani zmiany ustawien. CPU to roznica `TotalProcessorTime` podzielona
przez rzeczywisty czas i pomnozona przez 60. Dla dwoch uslug Windows odmowil
dostepu do `TotalProcessorTime`: podano osobny, rowniez 120-sekundowy odczyt
`Win32_Process.KernelModeTime + UserModeTime`, bez podnoszenia uprawnien.
Wczesnych zer wynikajacych z braku dostepu nie uznano za wynik pomiaru.

Debug UI porownano z rzeczywistym kodem UI sprzed P4 (`08bbde9`), zbudowanym
tymczasowo w tym samym worktree. Kazda proba miala osobny proces, 6 s rozgrzewki
i 30 s pomiaru; powtorzenie Ustawien mialo 15 s rozgrzewki. Okno widoczne
pozostawalo poza obszarem pulpitu (`AppWindow.IsVisible=true`); stan ukryty
uzywal `HideToTray`. FPS i EdgePanel byly wylaczone w obu wariantach, bez
PresentMon, globalnych skrotow, detekcji hover i hakow pierwszego planu.
Okna byly pokazywane bez aktywacji tylko na czas zrzutu i procesy zamykano.

To pomiary na uzywanym komputerze, z rownoleglymi buildami i testami. Nie sa
kontrolowanym benchmarkiem ani gwarancja procentowej poprawy. Working set to
pamiec rezydentna na koncu proby, nie limit ani prognoza uzycia pamieci.
Wynikow instalacji nie porownuje sie bezposrednio z izolowanym podgladem Debug.

## Wyniki

| Debug UI, stan | Przed: s CPU/min | Po: s CPU/min | Przed: MiB WS | Po: MiB WS |
|---|---:|---:|---:|---:|
| Ukryte | 0.500 | 0.094 | 197.38 | 195.58 |
| Glowna | 1.968 | 1.624 | 200.72 | 197.76 |
| Gry | 1.938 | 0.187 | 202.39 | 197.25 |
| Sesja | 3.249 | 0.875 | 206.03 | 203.03 |
| Wyniki | 3.218 | 0.500 | 197.45 | 195.34 |
| Aktualizacje | 1.781 | 0.844 | 202.96 | 197.43 |
| Ustawienia, powtorzenie | 1.654 | 0.218 | 205.35 | 200.20 |

Pierwsza proba Ustawien: 2.718 -> 3.585 s CPU/min, 203.72 -> 206.09 MiB WS.
Duzy przyrost WS podczas tej proby mogl wynikac z trwajacego startu lub presji
pamieci. Powtorzono pomiar z dluzsza rozgrzewka i zachowano oba wyniki.

Ukryte UI z utworzona, wlaczona nakladka, ktorej cel nie byl na pierwszym planie:
0.562 -> 0.219 s CPU/min, 203.21 -> 202.35 MiB WS (30 s; bez przechwytywania FPS
i sprzetowych samplerow w obu wariantach). Jest to koszt laczny procesu UI,
nie wydzielony koszt samej nakladki.

| Zainstalowany proces, odczyt przed zmianami | s CPU/min | WS poczatek -> koniec, MiB | Pomiar po |
|---|---:|---:|---|
| Dismode.UI | 0.669 | 301.66 -> 302.44 | Nie zastepowano instalacji |
| Dismode.SessionHost | 0.008 | 95.40 -> 95.43 | Nie zastepowano instalacji |
| Dismode.SystemAgent, CIM | 0.016 | 83.68 -> 83.36 | Nie zastepowano uslugi |
| Dismode.MemoryService, CIM | 0.155 | 63.83 -> 63.66 | Nie zastepowano uslugi |
| Dismode.MemoryOptimizer | 9.020 | 195.31 -> 200.64 | Poza zakresem zmian P4 |

## Oczekiwany efekt w uslugach — niezmierzony po wdrozeniu

| Mechanizm | Przed | Po | Efekt wynikajacy z kodu |
|---|---|---|---|
| SystemAgent, brak eksperymentu z timeoutem | Odczyt recovery co 60 s | Zdarzenie lub zapasowo 30 min + 60 s | Zwykle brak minutowego czytania dziennika; nadal 60 s podczas eksperymentu |
| MemoryService, wykrywanie uzytkownikow | Co 5 s | WTS + zapasowo 60 s | 12 -> 1 okresowych sprawdzen/min, plus rzeczywiste zdarzenia |
| MemoryService, automat wylaczony/pauza | Metryki co ustawione 5–300 s, domyslnie 15 s | Bez metryk, zapasowo 60 s lub zmiana ustawien | Domyslnie 4 -> 1 obudzen/min, 0 odczytow RAM/CPU |
| MemoryService, cooldown | Metryki co ustawione 5–300 s | Bez metryk; min(pozostaly cooldown, 60 s) | Bez zbednych pomiarow przed mozliwoscia wykonania operacji |
| UI, host bez sesji i widoczne okno | Zapytanie co 2 s | Najwczesniej po 15 s, na takcie 2 s | Odczyt zwykle co 16 s; natychmiast przy pokazaniu okna |
| UI, automat w zasobniku bez sesji | 6 s | 15 s z hakiem, 6 s bez haka | Zdarzenie pierwszego planu nadal uruchamia wykrywanie gry |
| Nakladka, opcja wlaczona, cel w tle | Licznik 200 ms | Zatrzymany licznik, wznowienie zdarzeniem | 300 -> 0 okresowych taktow/min w tym stanie |

Liczby wybudzen w tej tabeli sa wyliczone z harmonogramu, nie zmierzone ETW.

## Inwentaryzacja

Lokalizacje dotycza koncowego kodu. "Brak" oznacza brak wlasnej petli okresowej;
oczekiwanie asynchroniczne na zdarzenie lub dane nie jest pollingiem.
Uwzgledniono tez petle wykonywane przez zaleznosci SessionHost/UI.

| Plik:linia | Mechanizm i rytm | Kiedy pracuje / zmiana P4 |
|---|---|---|
| `src/Dismode.UI/MainWindow.xaml.cs:129` | DispatcherTimer, 2 s | Przed: rowniez zasobnik 6/15 s i minimalizacja. Po: tylko widoczne, niezminimalizowane okno; metryki RAM/CPU/dysku tylko na Glowna |
| `src/Dismode.UI/MainWindow.xaml.cs:133` | Nowy DispatcherTimer tla, 6/15 s | Tylko schowane/zminimalizowane okno: sesja, tray, automat; nie metryki widoku. Z hakiem foreground bez sesji 15 s, bez haka automat 6 s; podczas sesji dotychczasowe 6/15 s |
| `src/Dismode.UI/Services/WindowRefreshController.cs:48` | Zegar monotoniczny, minimum 15 s | Bramka pytan do hosta bez sesji; nie tworzy timera; pokazanie okna lub aktywna sesja omija bramke |
| `src/Dismode.UI/MainWindow.xaml.cs:254` | Bramka skanowania gier, 6 s | Nie osobny timer; wywolywana przez takt sesji/foreground. W tle tylko przy wlaczonym automacie |
| `src/Dismode.UI/MainWindow.xaml.cs:138` | DispatcherTimer, 400 ms | Debounce po edycji ustawien nakladki; zatrzymuje sie w handlerze, moze jednorazowo ponowic zajety zapis. Potrzebny zapis takze po schowaniu |
| `src/Dismode.UI/MainWindow.xaml.cs:146` | DispatcherTimer, 500 ms | Nakladka/FPS wlaczone, brak zarzadzanej sesji. Po: zatrzymany po braku celu/bledzie pomiaru, gdy jest hak foreground; zdarzenie wznawia. Bez haka zostaje fallback |
| `src/Dismode.UI/MainWindow.xaml:210`, `:2661` | Dwa ProgressRing, klatki animacji systemowej | Tylko operacja busy/aktualizacja. Po: IsActive=false przy ukryciu/minimalizacji; stan zadania zachowany na wznowienie |
| `src/Dismode.UI/PerformanceOverlayWindow.xaml.cs:71` | DispatcherTimer, 200 ms | Przed: gdy opcja nakladki wlaczona, rowniez gdy zaslonieta przez inny proces. Po: tylko pokazana nakladka; foreground wznawia bez oczekiwania na takt |
| `src/Dismode.Windows/Performance/OverlayTelemetrySampler.cs:110` | Worker, WaitOne 1000 ms / bezterminowo | CPU/GPU tylko przy aktywnej widocznej nakladce; po ukryciu SetActive(false), worker spi do sygnalu. Mechanizm juz istnial |
| `src/Dismode.Windows/Performance/OverlayTelemetrySampler.cs:205` | Bramki samplerow 1 s, retry po 30 s | W wyzej wymienionym workerze; brak osobnego timera |
| `src/Dismode.UI/Services/OverlayTargetWindow.cs:39` | Maksymalnie jeden probe uchwytu na 1 s | Cache odpytywany przez widoczna nakladke; brak osobnego timera |
| `src/Dismode.UI/Services/TacticalVfxService.cs:36` | Skonczony Storyboard: 400 ms, AutoReverse, RepeatBehavior(2) | Tylko akcja uzytkownika. Wejscia stron: 220/320 ms; flash 300 ms. Brak RepeatBehavior=Forever i CompositionTarget.Rendering w zakresie P4 |
| `src/Dismode.UI/MainWindow.xaml.cs:921` | Task.Delay 100 ms, do 30 s | Tylko oczekiwanie zleconego startu gry na koniec innej operacji; brak pracy w zwyklym idle |
| `src/Dismode.UI/Services/StartupGate.cs:336` | Thread.Sleep 250 ms, limit 8 s | Tylko start hosta i oczekiwanie na jego pipe |
| `src/Dismode.Core/Activation/UiActivationServer.cs:145` | Task.Delay 1 s | Retry tylko po bledzie utworzenia pipe; normalnie WaitForConnectionAsync. Deadline 10 s dotyczy pojedynczego podlaczonego klienta |
| `src/Dismode.UI/Services/MemoryOptimizerComponentService.cs:96` | Task.Delay 2 s | Jednorazowa obserwacja zleconego uruchomienia komponentu; nie petla |
| `src/Dismode.Windows/Sessions/LocalGameSessionOrchestrator.cs:1772`, `:1779`, `:1890` | Petla monitora, Task.Delay 1 s | Wylacznie aktywna/odtworzona sesja gry, w tym handoff i kontrola wyjscia; nie istnieje bez sesji. Bez zmian |
| `src/Dismode.Windows/Cpu/ProBalanceSupervisor.cs:409` | Task.Delay: konfiguracja calm/busy, domyslnie 1 s; preset gry 1 s/500 ms | Tylko wlaczony ProBalance aktywnej sesji; zatrzymywany przy jej zakonczeniu. Bez zmian |
| `src/Dismode.Windows/Profiles/ManualGameProfileLauncher.cs:210`, `:239` | Task.Delay 100/250 ms, domyslny timeout startu 10 s | Tylko identyfikacja uruchamianej gry / przejecie procesu launchera. Bez zmian |
| `src/Dismode.Windows/Processes/GracefulCloseApplicationAction.cs:188`, `:296` | Task.Delay 100 ms, timeout operacji | Tylko wykonanie/przywracanie dzialania sesji. Bez zmian |
| `src/Dismode.Windows/Services/WindowsServiceControlAdapter.cs:103` | Task.Delay 250 ms, timeout przejscia | Tylko zlecona zmiana stanu uslugi podczas operacji; brak petli w idle. Bez zmian |
| `src/Dismode.Windows/Processes/PresentMonFrameRateProvider.cs:178` | Jednorazowy Task.Delay, zadany czas benchmarku do 120 s | Tylko jawny pomiar gry. Zwykle CSV jest czytane asynchronicznie po nadejsciu danych, nie timerem. W testach/podgladach P4 nie uruchamiano PresentMon |
| `src/Dismode.SessionHost/SessionHostUpdatePreparation.cs:62` | Task.Delay 300 ms | Jednorazowo po zamknieciu UI podczas konserwacji/aktualizacji; brak idle loop |
| `src/Dismode.SystemAgent/SystemOptimization/SystemOptimizerRecoveryHostedService.cs:61`, `:66` | Petla recovery 60 s + nowe oczekiwanie zdarzenia/30 min | Bez oczekujacego timeoutu spi; przy oczekujacym eksperymencie nadal co 60 s |
| `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryServiceRuntime.cs:106` | Wykrywanie uzytkownikow: dawniej 5 s, teraz WTS + 60 s | Zawsze podczas pracy uslugi; powiadomienie WTS natychmiast przerywa oczekiwanie |
| `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryUserRuntime.cs:153` | Automat: ustawione 5–300 s (domyslnie 15 s) | Aktywny automat zachowuje rytm. Wylaczony/pauza: 60 s; cooldown: min(pozostaly czas, 60 s); zapis ustawien natychmiast budzi. Nieaktywne stany nie pobieraja metryk |
| `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/ServiceWakeSignal.cs:24` | Jeden timeout TimeProvider na oczekiwanie | Pomocnik dwoch powyzszych petli; powiadomienia laczone, anulowanie konczy oczekiwanie |
| `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryPipeServer.cs:32` | WaitForConnectionAsync, deadline odczytu 10 s | Oczekiwanie zdarzeniowe; tylko podlaczony klient ma timeout, bez okresowej pracy w idle |
| `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryOptimizerWindowsService.cs:58` | Task.Delay(Timeout.InfiniteTimeSpan) | Tylko console host czekajacy na anulowanie; bez okresowych wybudzen |

## Zrzuty i ograniczenia podgladu

Zachowano zrzuty poprzednika oraz wykonano nowe przed/po dla kazdej strony.
Zestawy `original-*` i `final-*` zawieraja skale renderowania 100% i 150%,
jasny motyw strony Sesja i nakladke na bialym oraz ciemnym tle przy jej skali
100% i 150%. Pomiary ekranu i klawiatury nie sterowaly stanem podgladu;
pokazywanie, minimalizacja i ukrycie byly zlecane programowo.

Natywna skala monitora wynosila 150%. Podglad 100% uzyskano przez skalowanie
korzenia XAML do 96 DPI, bez zmiany ustawien pulpitu. Nie jest to pelny test
przelaczenia DPI Windows miedzy monitorami. Minimalizacja zostala sprawdzona
na rzeczywistym oknie: visible=true, minimized=true, refreshTimer=false.
Puste biblioteka/historia i brak klatek sa celowe; nie uruchamiano gry.

Pliki PNG i surowe wyniki znajduja sie w przekazanym katalogu `ux-shots/p4`.
Zainstalowanych uslug nie zastepowano. Zmiany uslug po wdrozeniu i zachowanie
w prawdziwej grze pozostaja niezmierzone.

## Weryfikacja

| Komenda | Wynik |
|---|---|
| `dotnet build Dismode.sln -m` | Sukces, 0 ostrzezen, 0 bledow; bez tymczasowego kodu podgladu |
| `dotnet test tests/Dismode.UnitTests --no-build --no-restore` | 350/350 |
| `dotnet test tests/Dismode.RecoveryTests --no-build --no-restore` | 42/42 |
| `dotnet test tests/Dismode.SecurityTests --no-build --no-restore` | 10/10 |
| `dotnet test tests/Dismode.IntegrationTests --no-build --no-restore --filter "FullyQualifiedName~SystemOptimizerRecoveryHostedServiceTests\|FullyQualifiedName~ForegroundGameWatcherTests\|FullyQualifiedName~PerGameProfileCoordinatorTests"` | 25/25 |
| `dotnet build components/Dismode.MemoryOptimizer/Dismode.MemoryOptimizer.sln -m` | Sukces, 0 ostrzezen, 0 bledow |
| `dotnet test components/Dismode.MemoryOptimizer/tests/Dismode.MemoryOptimizer.Core.Tests --no-build --no-restore` | 93/93 |
| `dotnet test components/Dismode.MemoryOptimizer/tests/Dismode.MemoryOptimizer.Security.Tests --no-build --no-restore` | 12 sukcesow, 1 swiadomie pominiety test wymagajacy VM |
| `dotnet format Dismode.sln --verify-no-changes --no-restore` | Sukces |
| `dotnet format components/Dismode.MemoryOptimizer/Dismode.MemoryOptimizer.sln --verify-no-changes --no-restore` | Sukces |
| `git diff --check` | Sukces |

Unit/Recovery/Security uruchomiono wspolbieznie. Zmiany harmonogramow sprawdzaja
falszywe zegary i widocznosc, sygnaly wznowienia/anulowania oraz falszywy
provider klatek, ktory nie uruchamia PresentMon. Nowe regresje byly najpierw
odtworzone testami, potem usuniete. Wszystkie uruchomienia mialy limity czasu.

W kontynuacji 2026-11-01 ponownie zbudowano obie solucje i uruchomiono powyzsze
testy z limitem 1500 s na komende. Przejrzano kod harmonogramow i zachowane
zrzuty przed/po przy skalach 100% i 150%, w tym jasny motyw i oba tla nakladki.
Pomiary i PNG pochodza z poprzednich sesji tego samego zadania; nie zmieniano
od tego czasu kodu runtime P4 ani nie uruchamiano ponownie okien podgladu.

Poza scislym zakresem uslug konieczne byly: mala metoda odczytu cooldownu w
`Dismode.MemoryOptimizer.Core/Automation/AutoOptimizationPolicy.cs` oraz
rozbicie dwoch wierszy inicjalizatora w istniejacym `MemoryCleanupCooldownTests.cs`,
wymagane przez pelny `dotnet format`. Nie zmieniono logiki tego testu.
Praca pozostaje na przypisanej bazie 0.11.0; nie aktualizowano galezi do 0.11.1,
nie tworzono commitow i nie zmieniano zainstalowanych procesow.

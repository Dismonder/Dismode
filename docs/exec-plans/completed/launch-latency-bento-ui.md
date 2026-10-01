# Plan wykonawczy: szybki Play i Bento UI

Status: Tactical Deck v2 zainstalowany w 0.1.7; oczekuje na ocenę użytkownika  
Utworzono: 2026-08-01  
Źródło inspiracji: artykuł UX-MAN o trendach UX/UI 2025 oraz preferencja
użytkownika dotycząca Bento Box UI.

## Iteracja v2 — Tactical Deck

Wybrany cel wizualny:

- pełnoekranowy, ciemny command deck z nawigacją u góry;
- szeroki, filmowy hero wybranej gry i jeden dominujący przycisk Play;
- asymetryczny układ Bento dla przebiegu sesji, FPS/frametime, wpływu na
  system i zawsze widocznego recovery;
- jednolity język powierzchni, typografii, stanów oraz interakcji na każdym
  ekranie, nie tylko na stronie głównej;
- kompaktowy HUD zgodny z tym samym systemem wizualnym.

Zakres implementacji:

1. wydzielić tokeny kolorów, typografii, komponentów i ruchu;
2. przebudować `MainWindow.xaml`, zachowując istniejące kontrakty `x:Name`
   i zdarzenia, aby nie stworzyć równoległej logiki produktu;
3. przebudować Przegląd, Bibliotekę, Tryb gry, Historię, Aktualizacje,
   Diagnostykę i ustawienia jako jeden spójny produkt;
4. przebudować HUD bez utraty topmost/click-through/foreground-only;
5. rozszerzyć publikowanie i testy zasobów XAML o podkatalogi;
6. naprawić cleanup PresentMon i dodać test, aby ręczne wyjście z gry
   kończyło również proces pomiarowy przed finalizacją sesji;
7. przejść build, testy, format i kontrolę artefaktu publikowanego.

Brama akceptacyjna manualnego wyjścia z gry:

- monitor wykrywa brak całego zweryfikowanego drzewa procesu;
- wszystkie journalowane akcje procesu zostają skompensowane;
- PresentMon kończy się i jest zweryfikowany przed `ReconciliationComplete`;
- aktywna sesja znika, historia zostaje zapisana, a journal jest czysty;
- zachowanie jest identyczne niezależnie od zamknięcia gry przyciskiem w
  Dismode lub ręcznie w samej grze.

## Cel

Skrócić czas od kliknięcia Play do utworzenia procesu gry, zachowując pełną
walidację i recovery, oraz nadać głównym powierzchniom Dismode spójny,
nowoczesny układ Bento bez przebudowy natywnego stosu WinUI.

## Ustalenia z kodu

- Przed uruchomieniem gry wykonywane są kolejno: ponowny SHA-256 EXE,
  wszystkie zatwierdzone akcje tła i ich weryfikacja, a następnie trzeci
  SHA-256 w launcherze.
- Łagodne zamknięcie jednej aplikacji może czekać do 10 sekund; wiele akcji
  przed startem bezpośrednio zwiększa opóźnienie Play.
- Akcje są już wykonywane sekwencyjnie i transakcyjnie. Należy zmienić ich
  położenie względem startu gry, a nie tworzyć równoległy system.

## Decyzje

1. Zachować walidację profilu i trwały checkpoint recovery przed startem.
2. Uruchomić lub dołączyć do gry bezpośrednio po checkpointcie.
3. Dopiero po `GameLaunched` stosować ręcznie zatwierdzone akcje w tle,
   nadal sekwencyjnie: priorytet gry, a potem działania aplikacji.
4. Usunąć wyłącznie redundantne hashowanie launchera; niezależna weryfikacja
   tożsamości uruchomionego procesu pozostaje obowiązkowa.
5. Nie odłączać zadań od orkiestratora w tej iteracji. Gra startuje wcześniej,
   ale RPC kończy się dopiero po zweryfikowaniu akcji, co zapobiega wyścigowi
   apply/restore.
6. Zastosować z artykułu: minimalizm z charakterem, ograniczony glassmorphism,
   dynamiczne akcenty stanu i krótkie mikrointerakcje. Pominąć dekoracyjne 3D,
   emoji i fikcyjną personalizację AI.
7. Bento UI: jeden dominujący kafel bieżącej gry oraz mniejsze kafle stanu,
   telemetrii, recovery i szybkich działań, z mniejszą ilością tekstu.

## Walidacja

- test kolejności checkpointów: `GameLaunched` przed `ProcessesApplied`;
- test launchera potwierdza brak drugiego pre-launch hash przy zachowaniu
  weryfikacji tożsamości procesu;
- build Debug, pełne testy i format;
- bez uruchamiania rzeczywistej gry lub sterowania oknami użytkownika.

## Zrealizowane

- Przygotowano publikację pod pełny redesign wariantu 2: projekt UI odkrywa
  wszystkie skompilowane XBF rekursywnie, zachowuje podkatalogi i nie wymaga
  dopisywania każdej nowej strony lub słownika do ręcznej listy. Test zasobów
  obejmuje wszystkie źródłowe XAML-e poza `bin/obj`.
- Pipeline sesji ma kolejność `SnapshotComplete → GameLaunched →
  ProcessesApplied → SessionActivated`. Priorytet gry jest stosowany jako
  pierwszy krok po starcie, a działania aplikacji tła pozostają sekwencyjne.
- Launcher przyjmuje wyłącznie wewnętrzny dowód SHA-256 zweryfikowany przez
  orkiestrator. Usunięto trzeci odczyt całego EXE przed `Process.Start`, ale
  pełna tożsamość uruchomionego procesu nadal jest odczytywana i porównywana.
- Domyślną stroną aplikacji jest nowy przegląd Bento z Mica, dominującym
  kaflem Play, kaflami sesji, FPS, hostów, biblioteki, historii i recovery.
- Overlay ma nowy układ Bento 344×152: nagłówek gry, oddzielne kafle FPS i
  frametime oraz uproszczony wykres/status źródła.
- Z artykułu wdrożono minimalizm z akcentem, ograniczoną przezroczystość,
  dynamiczny kolor metryk i lekką mikrointerakcję wejścia. Nie dodano 3D,
  emoji ani pozornej personalizacji AI.
- Naprawiono wyścig startowy WinUI: praca SQLite rozpoczyna się dopiero po
  zakończeniu początkowego przebiegu `Loaded`/layout, bez wyłączania ustawień
  overlayu lub aktualizacji i bez diagnostycznych przełączników środowiska.

## Dowody

- izolowany `dotnet publish` unpackaged UI utworzył `App.xbf`,
  `MainWindow.xbf`, `PerformanceOverlayWindow.xbf` i `Dismode.UI.pri` bez
  zagnieżdżenia `publish\publish`; build UI: 0 ostrzeżeń, testy Unit: 38/38;
- wszystkie trzy pliki XAML są poprawnym XML;
- `dotnet build Dismode.sln --configuration Debug`: kod 0, 0 ostrzeżeń;
- `dotnet test Dismode.sln --configuration Debug --no-build`: 95/95;
- `dotnet format Dismode.sln --verify-no-changes --no-restore`: kod 0;
- regresja zapisanej reguły potwierdza `GameLaunched` przed
  `ProcessesApplied` oraz prawidłowe przywrócenie priorytetu procesu tła.
- hotfix brakującego klucza `DismodeCardBrush` przeszedł kontrolowany start,
  pełny build 0 ostrzeżeń i 106/106 testów; zainstalowane UI odpowiada,
  jest zmaksymalizowane i nie zapisuje nowego wyjątku startowego.
- Rzeczywisty Play uruchomił Roblox jako dziecko SessionHost; po zakończeniu
  sekwencyjnych działań backend raportował aktywną sesję oraz pomiar PresentMon
  `79,6 FPS / 12,56 ms` dla zweryfikowanego PID, bez ingerencji Codex w grę.
- Przed poprawką zwykły artefakt kończył się kodem `0xc000027b` podczas
  pierwszej inicjalizacji store; ta sama baza przeszła `quick_check` i odczyt
  z procesu PowerShell. Zarówno inicjalizacja w tle, jak i odroczenie o jeden
  przebieg dispatchera usunęły awarię. Czysty artefakt po poprawce działał
  przez 20 sekund pełnego startu bez zdarzenia Application Error 1000;
  publish i `dotnet format` projektu UI zakończyły się kodem 0.

## Pozostała ręczna kontrola

- ocenić Bento i overlay w aplikacji przy rzeczywistym DPI użytkownika;
- zmierzyć czas kliknięcie Play → pojawienie się procesu/okna gry na realnym
  tytule, bez uruchamiania gry wyłącznie przez Codex;
- po ocenie użytkownika skorygować gęstość, skalę i paletę, jeśli będzie to
  potrzebne.

## Wydanie 0.1.7

- Dashboard używa skalowanego płótna 1444×944 z geometrią referencji:
  rail, filmowy hero, karta sesji, FPS, wpływ na system i recovery.
- Miniatury nie mają nakładanych nazw, hero pokazuje lokalne grafiki i
  rzeczywiste dane launchera, a wybór w rail ma dokładnie jedną ramkę.
- Statusy FPS, SessionHost, SystemAgent i recovery są powiązane z realnym
  stanem; niedostępne 1% low, GPU i odzysk RAM pozostają jawnie `—`.
- Finalny build Release: 0 ostrzeżeń; pełna regresja: 128/128; instalator
  0.1.7 zainstalowany przez bezpieczną bramę aktualizacji, kod 0.
- Smoke zainstalowanego wydania: UI 0.1.7.0 responsywne, SessionHost `Ready`,
  SystemAgent `ReadOnly`, brak aktywnej sesji, journal czysty (861 wpisów),
  hash UI zgodny z payloadem.

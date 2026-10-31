# Dismode: dopracowanie wyglądu, Ustawień, optymalizatorów i wydajności

Stan: w toku (2026-10-31). Wydanie docelowe: 0.12.0. Baza: 0.11.0 (`main`).

## Cel

Po odświeżeniu 0.11.0 właściciel chce dopracowania: minimalistyczny panel
„Pod ręką”, ładniejsza strona Ustawienia i okna optymalizatorów, mniej zbędnych
tekstów, lepsze animacje i efekty, a Dismode ma mniej obciążać komputer w tle.

## Decyzje właściciela (2026-10-31)

1. **Panel „Pod ręką”: pływająca karta.** Karta na środku prawej krawędzi ekranu,
   wysokość dopasowana do treści, półprzezroczyste szklane tło. Na górze nazwa gry
   i ✕, duży licznik FPS z mini wykresem, przycisk Trybu gry, nakładka jako 5 ikon
   (wyłączona + 4 style), odczyty CPU/GPU z paskami, RAM i temperatura GPU, jedna
   linia „Ostatnio: …”. Te same funkcje co w 0.11.0, mniej tekstu, lepsza animacja
   i efekty, **bez dźwięków**.
2. **Uchwyt panelu:** w Ustawieniach suwak przezroczystości uchwytu przy krawędzi
   ekranu (podgląd na żywo, zapis w bazie).
3. **„Optymalizacje” obejmują trzy obszary:** sekcje na stronie Ustawienia, osobne
   okna System Optimizer i Memory Optimizer oraz wydajność samej aplikacji
   (mniej pracy w tle).
4. **Strona Ustawienia ma wyglądać lepiej**, w tym samym języku co strona Sesja
   (kafelki, krótkie teksty, szczegóły za „Więcej”).
5. Bez zmian: uczciwe liczby (tylko pomiary), teksty dla zwykłego gracza, nowe
   teksty w UI akceptuje właściciel przed wydaniem.

## Zakres prac (równoległe gałęzie, scalane do `claude/ux-polish`)

### P1. Panel „Pod ręką” i uchwyt

Pliki: `src/Dismode.UI/EdgePanel*.{xaml,cs}`, `src/Dismode.UI/Services/EdgePanel*.cs`,
`src/Dismode.UI/MainWindow.EdgePanel.cs`, `TacticalAudioService`, magazyn ustawień
panelu (`EdgePanelPreferences`, migracja 24), wiersz panelu w Ustawieniach.

- Karta ok. 300 DIP szerokości, zaokrąglona ze wszystkich stron, odsunięta od
  krawędzi, wyśrodkowana pionowo w obszarze roboczym; tło akrylowe (szkło) w ciemnym
  odcieniu aplikacji, z jednolitym tłem zastępczym, gdy Windows ma wyłączone efekty
  przezroczystości.
- Zawartość: nazwa gry + ✕ („Schowaj panel”); duża liczba FPS + mały wykres FPS
  z ostatniej minuty; przycisk „Włącz Tryb gry” / „Zakończ Tryb gry”; nakładka jako
  segment 5 ikon: wyłączona, „Tylko FPS”, „FPS + minimum”, „Kompakt w jednej linii”,
  „Pełne szczegóły” (nazwy w podpowiedziach i AutomationProperties) z przesuwanym
  wskaźnikiem wyboru; CPU i GPU z paskami obciążenia, RAM, temperatura GPU
  („—” bez pomiaru); jedna linia „Ostatnio: …” tylko, gdy jest sesja w historii.
- Usunięte teksty: „DISMODE”, „Pod ręką” (nagłówek), „Nakładka FPS”/„Wygląd nakładki”
  jako etykiety, „Teraz na komputerze”, przypis o kreskach, stopka ze skrótami.
  „Alt+D jest zajęty…” zostaje jako podpowiedź ikony ostrzeżenia.
- Animacje: wejście z prawej z zanikaniem i lekkim powiększeniem (ok. 240 ms, wygaszanie
  wykładnicze), kolejne wiersze wchodzą z opóźnieniem ok. 35 ms; zamykanie szybsze
  (ok. 160 ms); liczba FPS i paski płynnie przechodzą do nowych wartości; uchwyt przy
  najechaniu poszerza się i rozjaśnia, zanim panel się wysunie. Ustawienie „Efekty
  wizualne” i systemowe „Efekty animacji” wyłączają animacje (przejścia natychmiastowe).
- Bez dźwięków: panel i uchwyt nie odtwarzają żadnego dźwięku; martwy kod
  `PlayPanelOpen`/`PlayPanelClose` z syntezą i testami usunięty.
- Przezroczystość uchwytu: suwak 0–90 % (krok 5) pod przełącznikiem panelu
  w Ustawieniach; 0 % = pełny, nieprzezroczysty uchwyt; domyślna wartość odpowiada
  wyglądowi z 0.11.0; przy każdej wartości najechanie nadal otwiera panel.
- Wydajność: gdy panel jest schowany, nie ma odpytywania co 32 ms; wykrywanie
  najechania zdarzeniami okna uchwytu albo rzadkim sprawdzaniem tylko przy krawędzi;
  odczyty CPU/GPU i odświeżanie treści tylko przy otwartym panelu.

### P2. Strona Ustawienia

Pliki: obszar `DiagnosticsPage` w `MainWindow.xaml` i jego kod w `MainWindow.xaml.cs`.

- Nagłówek: „Ustawienia”, bez napisu „DISMODE / CENTRUM KONDYCJI PC” i długiego opisu.
- „Stan komputera”: zwarty pasek stanów (diagnostyka Windows/GPU, silnik, agent,
  system, procesor) z kolorową kropką i krótką nazwą; szczegóły w podpowiedzi lub za
  „Więcej”; „Sprawdź ponownie” jako przycisk z ikoną.
- „Gra i pomiar”: kafelki w stylu strony Sesja (ikona, nazwa, jedno zdanie korzyści,
  przełącznik, obecne długie opisy za „Więcej”): pomiar FPS, nakładka FPS, zgubione
  klatki, dane GPU, ograniczanie procesów tła, automatyczna optymalizacja wykrytej gry;
  reguły dla wszystkich gier jako mały wiersz z „Wyczyść”.
- „Wygląd i efekty”: motyw jako trzy karty z podglądem kolorów; dźwięki i efekty jako
  kafelki; panel przy krawędzi z przełącznikiem, „Alt+D” i suwakiem z P1.
- „Optymalizatory”: Memory Optimizer i System Optimizer jako dwa kafelki obok siebie
  (stan + przycisk otwarcia).
- „Aktualizacje”: jeden zwarty wiersz z wersją i „Sprawdź”.

### P3. Okna optymalizatorów

Pliki: `src/Dismode.SystemOptimizer/*` (okno główne) oraz
`components/Dismode.MemoryOptimizer/src/Dismode.MemoryOptimizer/*` (okno i menu w zasobniku).

- Ten sam język wizualny co Dismode: ciemny motyw, akcent cyjan, karty z promieniem 11,
  siatka 8 px, ikony Segoe Fluent Icons, jeden główny przycisk na ekran.
- Mniej tekstu: krótko i po ludzku, szczegóły techniczne za „Więcej” lub w podpowiedzi;
  wszystkie funkcje i nazwy dostępności zostają; nagłówki licencyjne GPL zostają.
- Okna nie wykonują pracy okresowej, gdy są schowane lub zminimalizowane.

### P4. Wydajność aplikacji w tle

Pliki: liczniki i animacje okna głównego (`MainWindow.xaml.cs` poza P2), nakładka,
SessionHost, usługi SystemAgent i MemoryService, poza plikami P1–P3.

- Pomiar przed i po (czas CPU na minutę, wybudzenia timerów, pamięć) dla procesów
  Dismode w spoczynku: okno schowane do zasobnika, brak gry.
- Okno schowane lub zminimalizowane: zatrzymane timery odświeżania, animacje
  w pętli i próbkowanie telemetrii; wznowienie przy pokazaniu okna.
- Usługi i SessionHost: bez częstego odpytywania, gdy nie ma sesji gry.
- Zachowanie funkcji bez zmian; każda zmiana z testem.

## Weryfikacja i odbiór

- Build bez ostrzeżeń, testy Unit/Recovery/Security/Integration, `dotnet format`.
- Zrzuty (100 % i 150 %): panel otwarty, uchwyt przy kilku wartościach suwaka,
  strona Ustawienia, okna System Optimizer i Memory Optimizer, przed i po.
- Liczby wydajności przed i po w raporcie.
- Właściciel akceptuje zrzuty i wszystkie nowe lub zmienione teksty przed wydaniem 0.12.0.

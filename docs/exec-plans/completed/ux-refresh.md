# Dismode: odświeżenie interfejsu dla zwykłego gracza

Stan: w toku (2026-10-31). Wydanie docelowe: 0.11.0.

## Cel

Program ma wyglądać jak launcher gier, a nie panel administratora. Gracz bez wiedzy
technicznej ma od razu rozumieć, co daje każdy element, chcieć go kliknąć i widzieć,
że Dismode coś realnie robi. Teksty krótkie, ludzkie, bez żargonu; szczegóły techniczne
schowane za „Więcej”.

## Decyzje właściciela (2026-10-31)

1. **Strona Sesja: kafelki jak launcher gier.** Duże kafelki z ikoną, jednym zdaniem
   „po co” i przełącznikiem; świecą po włączeniu (animacja + dźwięk); szczegóły
   techniczne w rozwijanym „Więcej”. Na górze duży przycisk START z wybraną grą
   i podsumowaniem „co Dismode zrobi”.
2. **Nakładka FPS: cztery style do wyboru** — „Tylko FPS”, „FPS + minimum” (FPS, czas
   klatki, 1% low), „Kompakt w jednej linii” (FPS | CPU | GPU | temp. GPU), „Pełne
   szczegóły” (jak dziś + wykres czasu klatki).
3. **Panel przy krawędzi ekranu (jak Overwolf):** cienki języczek przy prawej krawędzi,
   wysuwa się po najechaniu myszką albo skrótem (Alt+D), także nad grą w oknie bez
   ramki. W środku: aktualna gra i FPS, start/stop sesji, nakładka wł./wył. i wybór
   stylu, czyszczenie RAM, temperatury, ostatnia sesja.
4. **Instalator:** bez zmian w tej partii (pominięte przez właściciela).
5. Uczciwe liczby: tylko wartości zmierzone; żadnych wymyślonych „zysków”.

## Stan wyjściowy (rozpoznanie 2026-10-31)

Pełne rozpoznanie z odwołaniami plik:linia: `ux-recon.md` w scratchpadzie sesji
(ścieżka podana workerom). Najważniejsze fakty:

- „Odzyskane zasoby” na stronie głównej (`DashboardRecoveredMemoryText`) to martwy
  placeholder „—”, nic go nie ustawia.
- „Wykryte aplikacje” liczy zaznaczone, nie wykryte; „Szacowany odzysk RAM” mnoży
  working set przez wymyślone współczynniki 0,6 / 0,3. Do analizy obie karty pokazują 0.
- Realne dane: `SessionSummaries` (akcje, przywrócenia, czas gry w sekundach, FPS,
  1% / 0.1% low, przycięcia), working set kandydatów z analizy, liczba ograniczonych
  procesów w dzienniku recovery, wynik A/B benchmarku. `SystemMemoryCleaner` zwraca
  zmierzone zwolnione bajty, ale nie jest wywoływany.
- Dźwięki: 6 syntetycznych sinusów w `TacticalAudioService` (winmm PlaySound z pamięci),
  bez plików i licencji.
- Teksty: wiele technicznych opisów (recovery, ETW, PresentMon, CPU Sets, EcoQoS,
  „zatwierdzone działania”) i angielskie pozostałości, np. „The game session is being
  monitored locally”.
- Zakładka Gry: karta nagłówka (okruszki, tytuł, linia wykrywania, wyszukiwarka,
  4 przyciski) i karta „Ostatnio grane” zajmują około 300 px wysokości przed listą gier.

## Zakres prac

### A. Strona Sesja i reszta interfejsu (worker UI)
- Strona Sesja według decyzji 1: kafelki opcji sesji (pamiętaj ustawienia, auto-optymalizacja,
  rdzenie wydajne, nie usypiaj ekranu, reguły tła po starcie, maks. odświeżanie), karta
  aplikacji w tle z czytelnym stanem, karta HUD z wyborem stylu nakładki (4 style).
- Zakładka Gry: jedna zwarta belka (tytuł, wyszukiwarka, licznik, przyciski) i niski
  pasek „Ostatnio grane” zamiast dużej karty; pasek informacyjny „Gra startuje” zwijany.
- Teksty dla zwykłego gracza na stronach Główna, Gry, Sesja; szczegóły w „Więcej”;
  usunięcie angielskich pozostałości.
- Uczciwe wartości: „Odzyskane zasoby” z realnych danych (np. suma czasu gry, liczba sesji
  z przywróconym tłem, średni FPS / 1% low z ostatnich sesji); karty planu przemianowane
  na to, co naprawdę pokazują (np. „Aplikacje w tle: N”, „Zajmują teraz: X MB”) i liczone
  z wykrytych, nie tylko zaznaczonych.

### B. Dźwięki (worker SFX)
- Lepsze, wciąż syntetyczne (bez plików i licencji) brzmienia w `TacticalAudioService`:
  obwiednie ADSR, warstwy harmoniczne, lekki chorus/detune, krótkie „whoosh” dla
  startu sesji, przyjemny „tick” przełącznika, akord sukcesu; głośność dopasowana,
  bez trzasków (fade-in/out). Nowy dźwięk wysunięcia panelu bocznego. Zachować API.

### C. Style nakładki (worker overlay)
- `PerformanceOverlayStyle`: cztery style z decyzji 2 (migracja starych wartości
  zachowuje dzisiejszy wygląd jako „Pełne szczegóły”). Okno nakładki układa treść
  według stylu; rozmiar okna dopasowany do treści; przezroczystość z 0.10.1 bez zmian.

### D. Panel przy krawędzi (worker edge panel)
- Nowe okno panelu według decyzji 3: języczek przy prawej krawędzi (bez kradzieży fokusu
  z gry), wysuwanie najechaniem i skrótem globalnym (Alt+D, `RegisterHotKey`), animacja,
  chowanie po odjechaniu / Esc / drugim skrócie. Opcja w Ustawieniach (domyślnie wł.)
  i wybór krawędzi tylko prawa w tej partii.

## Weryfikacja
- Build, testy bez Live, format, `git diff --check`.
- Zrzuty każdego zmienionego widoku z wersji deweloperskiej (bez sterowania ekranem
  użytkownika): zrzut okna własnym skryptem `PrintWindow`/`CopyFromScreen`.
- Wszystkie nowe teksty wypisane dosłownie w PR do akceptacji właściciela.

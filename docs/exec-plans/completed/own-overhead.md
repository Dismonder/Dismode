# Narzut własnych procesów Dismode

Scalone w PR #12 (wydanie 0.9.0).
Pomiar: `OwnOverheadMeasurements`, kategoria `Perf`, rozgrzewka 16 wywołań,
Stopwatch oraz alokacje wątku; dla async alokacje całego procesu (precise).
Poniżej zapisane rzeczywiste przebiegi przed zmianą i po niej, te same fixture i N.
Czasy to czas ścienny metody, nie procent CPU ani wynik pomiaru gry.
Przebiegi przed zmianami: `%TEMP%/dismode-perf-item<ID>-before.log`.
Ostateczny przebieg po zmianach: `%TEMP%/dismode-own-perf.log`, bez równoległych
komend weryfikacji. Czasy zmieniają się między przebiegami; budżety dotyczą alokacji.

| Punkt / fixture / rytm / N | Przed: B/wyw.; ms/wyw.; B/s; ms/s | Po: B/wyw.; ms/wyw.; B/s; ms/s | Stan |
|---|---|---|---|
| 1: zatrzymany konsument, odtworzenie 240 wierszy na sekundę, N=100 | 472320; 0,468293; 472320; 0,468293 | 0; 0,000010; 0; 0,000010 | done |
| 2: główny + pomocniczy strumień, także DisplayedTime, 1 Hz, N=50 | 4816015,20; 0,588000; 4816015,20; 0,588000 | 1348,80; 0,217286; 1348,80; 0,217286 | done; wynik po również z optymalizacją 4 |
| 3: CSV 2.5.1, 23 kolumny, cytowania, 240 Hz, N=100000 | 1968; 0,002432; 472320; 0,583622 | 0; 0,001224; 0; 0,293866 | done |
| 4: dokładne okno 2 s, 240 Hz, N=100000 | 40; 0,000282; 9600; 0,067775 | 0; 0,000224; 0; 0,053681 | done |
| 5a: Capture, stały natywny obraz 300 procesów, 1 Hz, N=1000 | 58784; 0,154194; 58784; 0,154194 | 21657,22; 0,139173; 21657,22; 0,139173 | done; główny zysk w alokacjach |
| 5b: supervisor + engine, stałe 300 procesów, 1 Hz, N=1000 | 155416; 0,533806; 155416; 0,533806 | 46284,29; 0,323577; 46284,29; 0,323577 | done |
| 9: drzewo 3 procesów gry wśród 300 procesów, 1 Hz, N=1000 | 3424; 0,005623; 3424; 0,005623 | 688; 0,003885; 688; 0,003885 | done |
| 8: resolver Process/HasExited/MainWindowHandle na własnym procesie, syntetyczny HWND, 5 Hz, N=1000 | 464; 0,061379; 2320; 0,306893 | 92,80; 0,012692; 464; 0,063460 | done; natywny rendering WinUI poza pomiarem |
| 6: SQLite, 32 wykluczenia, N=1000; ukryty tray + automatyzacja | 9225,70; 0,041664; 2460,19; 0,011110 (1/5+1/15 Hz) | 808; 0,005258; 80,80; 0,000526 (1/30+1/15 Hz) | done |
| 7: indeks 64 gier, co 6 s, N=10000 | 12776; 0,010358; 2129,33; 0,001726 | 0; 0,000073; 0; 0,000012 | done |
| 10: dziennik odzyskiwania | — | — | not done: pominięty na polecenie właściciela |

Wszystkie mierzone punkty przekroczyły próg pomijalności przed zmianą (alokacje
lub czas). Budżety alokacji w testach: około 2× wynik po zmianie, minimalnie 8 B
dla ścieżek bez alokacji. Punkt 2 po samej zmianie: 14900,80 B i 0,252668 ms
na granicę próbki; dalszy spadek pochodzi z punktu 4.

## Poprawność i ryzyka

- 1: fake provider odtwarza koszt parsera, bez procesu PresentMon. Test obejmuje
  trzy przyczyny stopu oraz próbkę w toku i spóźnioną próbkę po stopie.
  Zatrzymanie prawdziwego procesu zachowuje dotychczasowe kontrole właściciela.
- 2: zerowanie histogramów zachowuje granice próbek i semantykę zmiany dominacji.
  Cena: bufory pomocniczych histogramów pozostają do wygaśnięcia strumienia;
  jeden histogram to ~2,4 MB, dwa ze śledzeniem wyświetlania ~4,8 MB.
  Nie jest to redukcja całej pamięci rezydentnej o liczbę B/s z tabeli.
- 3: zachowany dosłowny klucz swapchaina, także nienumeryczny i cytowany.
  Klucza numerycznego nie wprowadzono, aby nie łączyć dotąd odrębnych napisów.
  Wiersz z nowym kluczem nadal tworzy napis; odczyt ReadLineAsync pozostaje.
- 4: usuwanie podczas enumeracji Dictionary jest obsługiwane w .NET 10;
  test sprawdza dokładną liczbę, znaczniki czasu i wygaśnięcie strumienia.
- 5: bufor rośnie i ma SafeHandle/Dispose; supervisor zwalnia źródło po stopie.
  Nazwy są cache'owane po PID + czasie utworzenia, nie po samym PID.
  Fixture wypełnia bufor stałymi danymi: nie mierzy czasu rzeczywistego syscalla.
  Kolejność decyzji przy równym obciążeniu pozostała stabilna.
  Pełny zestaw ujawnił wcześniejszą niezgodność nazwy `.scr` z Process.ProcessName;
  poprawiono normalizację (usuwamy tylko `.exe`) i dodano stały przypadek regresji.
- 9: snapshot znanych procesów jest tylko do odczytu; bufory robocze są osobne
  dla równoległych obserwacji. Kontrole PID, czasu, użytkownika i sesji zachowane.
- 8: własność HWND sprawdzana przy użyciu; pełne ponowne wyszukanie co sekundę,
  natychmiast po zmianie PID lub nieprawidłowym HWND. Zmiana głównego okna przy
  nadal poprawnym starym HWND może być zauważona do sekundy później.
  Wspólne pędzle są lokalne dla okna. Ukryty pulpit zbiera historię, rysuje po pokazaniu.
  B/s z tabeli dotyczą resolvera; nie przypisujemy liczb niemierzonemu renderingowi.
- 6: stałe połączenie i PRAGMA data_version wykrywają zapisy innych połączeń;
  zapis lokalny również unieważnia cache dopiero po sukcesie. Test dwóch writerów.
  Zmiana tray 5→30 s dotyczy ukrytego okna; pokazanie od razu odświeża status.
- 7: unieważnianie przy zmianach kolekcji profili (ich tożsamość jest niezmienna).
  Wspólna bramka skanowania zachowuje nominalne 6 s i minimum 1 s przy żądaniach
  foreground; żądanie podczas skanu pozostaje oczekujące. Nie zmieniono samej
  enumeracji procesów ani reguł rozpoznawania gier.

## Weryfikacja

- `dotnet build Dismode.sln`: 0 błędów, 0 ostrzeżeń.
- `dotnet test Dismode.sln --filter "TestCategory!=Live"`: 753 zaliczone,
  5 pominiętych przez istniejące warunki uruchomienia, 0 błędów.
- `dotnet build components/Dismode.MemoryOptimizer/Dismode.MemoryOptimizer.sln`:
  0 błędów, 0 ostrzeżeń; obejmuje też tray i usługę spoza głównego rozwiązania.
- `dotnet test components/Dismode.MemoryOptimizer/Dismode.MemoryOptimizer.sln`:
  94 zaliczone, 1 pominięty test wymagający jawnej flagi VM, 0 błędów.
- `dotnet test tests/Dismode.IntegrationTests --filter TestCategory=Perf --logger
  "console;verbosity=detailed"`: 9/9, wydrukowane liczby i wszystkie budżety zaliczone.
- `dotnet format Dismode.sln --verify-no-changes`: exit 0.
- `git diff --check`: exit 0. Końcowy stan: zero procesów Dismode.ProcessTestHarness.

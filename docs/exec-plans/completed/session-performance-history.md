# Plan wykonawczy: rzetelny wynik wydajności w historii

Status: ukończono i wdrożono
Utworzono: 2026-07-29

## Cel

Domknąć brak Bramy H: po zakończeniu sesji zachować i pokazać wyłącznie
rzeczywiście zmierzone statystyki FPS/frametime, bez estymowania przy braku
danych i bez przedstawiania pojedynczego odczytu jako wyniku całej sesji.

## Implementacja

- walidowany model statystyk interwałowych w `Dismode.Core`;
- lekki akumulator próbek już pobieranych przez SessionHost, bez drugiego
  źródła telemetrii;
- liczba próbek, średni FPS, średni frametime, najniższa próbka FPS i
  najwyższa próbka frametime;
- migracja SQLite v5 zachowująca dotychczasową historię jako „brak danych”;
- jawny opis metody na karcie historii.

## Walidacja

- testy nieprawidłowych i pustych danych;
- test agregacji tylko próbek `Measuring`;
- round-trip SQLite wraz z migracją;
- build Debug/Release, pełny właściwy zestaw testów i format.

## Ograniczenia

- wynik jest średnią z równych próbek interwałowych, a nie benchmarkiem
  porównawczym ani 1% low;
- po awarii hosta statystyka obejmuje próbki dostępne po recovery, dopóki
  okresowy checkpoint agregatu nie zostanie osobno zaprojektowany;
- brak wystarczających próbek pozostaje jawnie oznaczony.

## Wynik

- model odrzuca puste, niefinitywne i niespójne statystyki;
- akumulator przyjmuje wyłącznie prawidłowe próbki `Measuring`;
- pełny test potwierdza przepływ próbka → zakończenie sesji → SQLite →
  Historia;
- migracja v4→v5 zachowuje starszą historię bez dopisywania fałszywego FPS;
- build Debug/Release: 0 ostrzeżeń, format bez zmian, pełna regresja 90/90;
- po potwierdzeniu braku aktywnej sesji i czystego journalu wydanie
  opublikowano do `artifacts/Dismode-App`; smoke potwierdził SQLite v5,
  SessionHost `Ready`, SystemAgent `ReadOnly` i zmaksymalizowane UI.

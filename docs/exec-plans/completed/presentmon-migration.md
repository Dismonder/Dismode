# Plan wykonawczy: migracja telemetrii do PresentMon

Status: zakończony i opublikowany
Utworzono: 2026-07-29

## Cel

Całkowicie usunąć zależność Dismode od poprzedniego, zewnętrznego miernika i
zastąpić ją przypiętym, open-source'owym PresentMon. Pomiar ma dotyczyć
wyłącznie zweryfikowanych PID aktywnej gry, nie wymagać bezpośredniej
mutacji z UI i uczciwie raportować brak danych.

## Zakres

1. Usunąć lokator, launcher, parser pamięci współdzielonej i stare
   nazewnictwo.
2. Dodać kontrolowany provider PresentMon z wersją, hashem i zamkniętą
   listą argumentów.
3. Powiązać cykl życia procesu pomiarowego z SessionHost i aktywną sesją.
4. Zachować obecny kontrakt FPS/ms, aby nie tworzyć równoległego systemu.
5. Dołączyć wymagane informacje licencyjne MIT i third-party notices.
6. Uruchamiać Dismode jako okno zmaksymalizowane.

## Inwarianty

- UI nie uruchamia dowolnych plików wykonywalnych.
- PresentMon jest stałym komponentem wydania o sprawdzonym SHA-256.
- Provider odrzuca PID spoza aktywnej, zweryfikowanej sesji.
- Brak próbki lub błąd ETW nie generuje wymyślonego FPS.
- Zamknięcie lub recovery sesji kończy należący do niej proces pomiarowy.
- Nie wstrzykujemy kodu do gry i nie obchodzimy anti-cheat.

## Walidacja

- build Debug i Release bez ostrzeżeń;
- wąskie testy parsera oraz cyklu życia providera na kontrolowanym procesie;
- kontrola braku plików i nazw starego miernika w kodzie oraz wydaniu;
- smoke wydania bez uruchamiania prawdziwej gry;
- potwierdzenie czystego journalu i gotowości obu pipe'ów.

## Ryzyka

- ETW może wymagać członkostwa w `Performance Log Users` albo instalowanego
  komponentu usługowego; stan musi być jawny w UI.
- Format stdout/CSV PresentMon jest kontraktem zewnętrznym, więc parser musi
  być wersjonowany i odporny na częściowe wiersze.
- Rzeczywistego pomiaru nie wolno ogłaszać jako zaliczonego bez kontrolowanej
  aplikacji renderującej klatki.

## Stan wykonania

- Usunięto stary provider, lokator, UI, diagnostykę, testy i nazewnictwo.
- Dodano oficjalny `PresentMon-2.5.1-x64.exe`, licencję MIT, third-party
  notices oraz podwójną kontrolę rozmiaru i SHA-256.
- Provider używa wyłącznie PID drzewa gry, CSV v2 i dwusekundowego okna
  prawdziwych `FrameTime`; kończy własny proces przy restore, recovery i
  dispose hosta.
- SessionHost wymaga UAC; Launcher, SystemAgent i UI pozostają
  niepodwyższone. UI uruchamia się zmaksymalizowane.
- Build Debug i Release: 0 ostrzeżeń. Testy parsera/integralności: 3/3.
  Oficjalny strumień ciągły zwrócił nagłówek i klatkę przed zakończeniem
  procesu.
- Przed publikacją SessionHost potwierdził brak aktywnej sesji, journal nie
  miał niedokończonych sesji i kończył się checkpointem recovery `10`.
- Kandydat został opublikowany do `artifacts/Dismode-App`, a poprzednie
  wydanie zachowano jako
  `artifacts/Dismode-App.backup-20260729-195010`.
- Smoke opublikowanego wydania potwierdził jeden SessionHost z tokenem
  `Elevated`, jeden SystemAgent, jedno UI z tokenem `Standard` i stanem okna
  `Maximized`, gotowość obu pipe'ów, 288 procesów, 316 usług i brak aktywnej
  sesji.
- Opublikowany PresentMon ma rozmiar `956768` i SHA-256
  `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`.
  W wydaniu ani kodzie nie pozostały pliki lub nazwy poprzedniego miernika.
  Własny proces PresentMon pozostaje wyłączony bez aktywnej sesji gry.

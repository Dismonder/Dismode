# Plan wykonawczy: zapisane optymalizacje w przycisku Play

Status: ukończono
Utworzono: 2026-07-29

## Cel

Przycisk Play w Bibliotece ma nie tylko uruchamiać grę z zapisanym
priorytetem, lecz również stosować wcześniej zatwierdzone reguły procesów
tła dla tej konkretnej gry.

## Inwarianty

- szybki Play nie tworzy nowych reguł i nie podejmuje niezatwierdzonych
  decyzji;
- reguły są dopasowywane po pełnej ścieżce EXE, nie po samej nazwie lub PID;
- każdy aktualny proces ponownie przechodzi klasyfikację, kontrolę sesji,
  czasu startu i pełną walidację w SessionHost;
- reguła `Ignoruj` nigdy nie tworzy działania;
- nieaktualna albo obecnie niedozwolona reguła jest bezpiecznie pomijana i
  widoczna w podsumowaniu, zamiast blokować uruchomienie gry;
- limit 64 działań i Hard Safety Policy pozostają bez zmian.

## Implementacja

- typowany interfejs odczytu preferencji gry;
- resolver zapisanych ścieżek względem bieżącej inwentaryzacji procesów;
- jawna flaga kontraktu `use_saved_background_rules`;
- SessionHost buduje i ponownie zatwierdza plan, nie ufając decyzji UI;
- przycisk Play używa flagi, a zwykły ekran planu nadal wysyła ręcznie
  wybrane działania.

## Walidacja

- test resolvera: exact-path, `Ignoruj`, chronione procesy, sesja i
  ograniczenia działania;
- test orkiestratora: zapisana reguła rzeczywiście zmienia kontrolowany
  process harness i zostaje przywrócona;
- build Debug/Release, pełny właściwy zestaw testów, format i smoke IPC/UI.

## Wynik

- resolver i pełny przepływ przeszły 4/4 nowe scenariusze;
- pełna regresja przeszła 94/94 testy;
- build Debug i Release zakończyły się bez ostrzeżeń, format jest czysty;
- kontrolowany proces przeszedł rzeczywistą zmianę
  `Normal → BelowNormal → Normal`;
- wydanie opublikowano do `artifacts/Dismode-App`, a smoke potwierdził
  gotowe pipe'y, zmaksymalizowane UI i brak aktywnej sesji.

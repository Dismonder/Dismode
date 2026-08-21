# Plan wykonawczy: niezawodny FPS i nakładka HUD v2

Status: zakończony
Utworzono: 2026-07-29

## Cel

1. usunąć wyciek sesji ETW pozostawianych po wymuszonym zatrzymaniu
   PresentMon;
2. przywrócić rzeczywisty, strumieniowy pomiar FPS dla aktywnej gry;
3. zastąpić obecną nakładkę kompaktowym, czytelnym HUD-em gamingowym;
4. zachować brak wstrzykiwania kodu, brak przechwytywania wejścia i brak
   wymyślonych wartości.

## Ustalenia

- aktywna sesja Roblox śledzi prawidłowy PID `19436`;
- własny PresentMon działa, ale GameShift otrzymuje zero klatek;
- niezależne próby konsolowe kończą się komunikatem o utraconych zdarzeniach
  ETW;
- na hoście pozostało siedem sesji `GameShift-18416-*` po nieistniejącym
  SessionHost oraz jedna sesja bieżącego hosta;
- źródłem wycieku jest losowa nazwa sesji przy każdym starcie połączona z
  wymuszonym `Process.Kill`, bez zakończenia nazwanej sesji ETW.

## Implementacja

- stabilna, ograniczona do użytkownika nazwa sesji PresentMon;
- `--stop_existing_session` przed każdym przechwytywaniem;
- jawne zakończenie nazwanej sesji ETW podczas stop/dispose;
- limit czasu oczekiwania na pierwszą klatkę, diagnostyka i kontrolowany
  restart zamiast nieskończonego stanu „Oczekiwanie”;
- lżejszy profil przechwytywania tylko dla metryk potrzebnych do FPS;
- HUD v2: wyraźne FPS, frametime, stan źródła, subtelny wykres klatek i
  kompaktowa obudowa bez dużego bloku tekstu.

## Walidacja

- test cyklu start/stop bez osieroconej sesji ETW;
- test restartu po braku pierwszej klatki;
- wąskie testy parsera i selekcji procesu;
- build Debug/Release;
- smoke opublikowanego wydania i rzeczywisty odczyt sesji użytkownika bez
  uruchamiania ani zamykania gry przez Codex.

## Wynik

- usunięto siedem osieroconych sesji ETW poprzedniego SessionHost; bez
  restartowania gry bieżący pomiar zaczął zwracać prawdziwe klatki;
- build Debug i Release zakończyły się bez ostrzeżeń;
- pięć wąskich testów parsera, selekcji PID i stabilnej nazwy sesji przeszło
  5/5;
- po kontrolowanym restarcie wyłącznie składników GameShift recovery
  zachowało Roblox PID `19436`, a nowy backend utworzył dokładnie jedną
  stabilną sesję `GameShift-9CD59278A121E901`;
- końcowy odczyt zwrócił `240,1 FPS` i `4,16 ms` dla zweryfikowanego PID;
- HUD v2 został opublikowany i zweryfikowany jako widoczny, topmost,
  click-through oraz nieaktywujący; pełny render przy DPI 144 ma 492×216 px
  i nie jest ucięty;
- kopie sprzed podmian:
  `artifacts/GameShift-App.ui-overlay-v2-backup-20260729-204516`,
  `artifacts/GameShift-App.ui-dpi-backup-20260729-204820` oraz
  `artifacts/GameShift-App.backend-etw-backup-20260729-205007`.

## Ryzyka

- ekskluzywny fullscreen może zasłonić zwykłe okno topmost; HUD pozostaje
  przeznaczony dla windowed/borderless;
- Windows/sterownik może nadal nie emitować obsługiwanych zdarzeń dla
  konkretnej gry; UI musi pokazać wtedy konkretną diagnostykę, nie fałszywe
  FPS.

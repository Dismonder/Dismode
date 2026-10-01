# Plan wykonawczy: widoczność i ustawienia nakładki

Status: ukończono i wdrożono
Utworzono: 2026-07-29

## Cel

1. pokazywać HUD wyłącznie, gdy okno śledzonej gry jest na pierwszym planie;
2. dodać przełącznik włącz/wyłącz;
3. dodać regulację przezroczystości i wielkości;
4. pozwolić wybrać jeden z czterech rogów monitora gry;
5. trwale zapisywać ustawienia w istniejącej bazie użytkownika.

## Implementacja

- typowane preferencje globalne i migracja SQLite v4;
- odświeżanie widoczności co 200 ms na podstawie `GetForegroundWindow`
  i PID źródła FPS;
- skala 75–150%, przezroczystość 35–100% oraz cztery pozycje;
- ustawienia WinUI z krótkim opóźnieniem zapisu, aby suwak nie tworzył
  wielu równoległych transakcji;
- zachowanie topmost, click-through, no-activate i per-monitor DPI.

Korekta po zgłoszeniu użytkownika:

- krycie nie jest już ustawiane na korzeniu XAML, co odsłaniało
  nieprzezroczyste ciemne tło hosta;
- okno otrzymuje `WS_EX_LAYERED`, a suwak steruje jego natywnym alpha przez
  `SetLayeredWindowAttributes`;
- wartość jest odczytywana przez `GetLayeredWindowAttributes` i musi
  odpowiadać żądaniu, inaczej HUD fail-closed zgłasza niedostępność.

## Walidacja

- test modelu i round-trip ustawień SQLite;
- build Debug/Release oraz format;
- wdrożenie tylko UI/Data/Core bez przerywania gry;
- read-only smoke okna: ukrycie poza grą, pokazanie po powrocie do gry,
  rozmiar/pozycja i zapis ustawień.

## Wynik

- build Debug i Release: 0 ostrzeżeń;
- format verification i `git diff --check`: kod 0;
- pełny zestaw: 83/83 testy po usunięciu osieroconego procesu własnego
  harnessu;
- SQLite użytkownika został bez błędu podniesiony do wersji 4;
- UI z wydania
  `artifacts/Dismode-App-overlay-settings-20260729-212445` działa jako PID
  `21144`; Roblox, SessionHost, SystemAgent i PresentMon zachowały swoje PID;
- odczyt IPC: Roblox PID `19436`, `159,5 FPS`, `6,27 ms`;
- read-only enumeracja Win32 potwierdziła foreground Robloxa i HUD 328×144
  w prawym górnym rogu;
- nie przełączano okien i nie sterowano grą; ukrycie poza grą wynika z
  fail-closed porównania `GetForegroundWindow` co 200 ms z dokładnym PID
  źródła FPS.

## Wdrożenie

Po naturalnym zakończeniu gry read-only IPC potwierdził brak aktywnej sesji,
a journal był czysty. Spójne wydanie zostało opublikowane do
`artifacts/Dismode-App`, poprzednie zachowano jako
`Dismode-App.backup-20260729-221808`, a skrót pulpitu odświeżono. Smoke
potwierdził zmaksymalizowane, responsywne UI oraz brak widocznego HUD przy
braku aktywnej gry.

## Ryzyka

- ekskluzywny fullscreen nadal może zasłonić zwykłe okno topmost;
- niektóre gry mogą przekazywać fokus do procesu-hostowania innego niż
  renderujący PID; brak pewnego dopasowania ma ukrywać HUD zamiast pokazywać
  go nad pulpitem.

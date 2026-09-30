# Plan wykonawczy: natychmiastowe zamknięcie, nakładka i tryb agresywny

Status: zakończony
Utworzono: 2026-07-29

## Cel

Rozszerzyć istniejący przepływ sesji bez tworzenia równoległego systemu:

1. przycisk zamknięcia ma natychmiast kończyć wyłącznie zweryfikowane procesy
   aktywnej gry;
2. prawdziwe FPS i czas klatki z PresentMon mają być widoczne w natywnej,
   nieaktywującej nakładce;
3. profil Agresywny ma wybierać większą liczbę bezpiecznych aplikacji tła i
   stosować najsilniejsze dostępne, odwracalne ograniczenia CPU/RAM;
4. osierocona sesja bez działającego procesu gry ma automatycznie przejść
   przez recovery.

## Inwarianty

- Brak dowolnego PID lub ścieżki w IPC; cel pochodzi wyłącznie z aktywnej,
  wcześniej zweryfikowanej sesji i jest ponownie sprawdzany przed operacją.
- Natychmiastowe zakończenie nie obejmuje anti-cheat, usług, procesów
  systemowych ani procesów spoza śledzonego drzewa gry.
- Tryb Agresywny nie wyłącza zabezpieczeń, sterowników, krytycznych usług ani
  anti-cheat i nie używa sztucznego czyszczenia RAM/standby list.
- Każda odwracalna zmiana pozostaje journalowana i przywracana; zakończenie
  procesu gry jest jawną, nieodwracalną intencją użytkownika.
- Nakładka nie przechwytuje klawiatury ani myszy i nie wstrzykuje kodu do gry.
- Brak próbki PresentMon oznacza jawny brak danych, nigdy wymyślone FPS.

## Walidacja

- wąski test natychmiastowego zakończenia na własnym process harnessie;
- test automatycznego recovery osieroconej sesji;
- test mapowania profilu Agresywnego i ochrony celów;
- build Debug/Release oraz smoke opublikowanego wydania;
- kontrola tokenów, obu pipe'ów, stanu nakładki i braku aktywnego własnego
  PresentMon poza sesją gry.

## Ryzyka

- Wymuszone zakończenie gry może utracić niezapisany stan; UI musi nazwać tę
  operację jednoznacznie.
- Okno topmost jest widoczne w trybie okienkowym i borderless; ekskluzywny
  fullscreen może wymagać przełączenia gry na borderless.
- PresentMon zależy od zdarzeń ETW gry i może być ograniczony przez jej model
  renderowania lub anti-cheat.

## Stan wykonania

- `CloseGameRequest` ma jawną flagę force bez PID ze strony UI; SessionHost
  ponownie sprawdza pełną tożsamość, kończy tylko główny/interaktywny proces
  śledzonego drzewa i omija nazwy anti-cheat.
- PresentMon wybiera jeden proces z oknem i największym working set, co usuwa
  błąd wielokrotnego `--process_id` kierujący Roblox na CrashHandler.
- Recovery zmienia stan sesji przed zatrzymaniem providera, usuwając wyścig,
  który mógł pozostawić osierocony PresentMon.
- Dodano osobne okno WinUI nakładki oraz domyślnie włączoną opcję w Trybie
  gry.
- Profil Agresywny analizuje do 64 kandydatów, zamyka restartowalne aplikacje
  z oknem i ogranicza pozostałe przez `BelowNormal + EcoQoS`.
- Build Debug/Release: 0 ostrzeżeń. Wąskie testy: 3/3. Journal przed
  publikacją: czysty, 353 rekordy.
- Wydanie opublikowano do `artifacts/Dismode-App`; kopia:
  `artifacts/Dismode-App.backup-20260729-202043`.
- Po ręcznym zatwierdzeniu UAC końcowy smoke potwierdził jedno responsywne UI,
  jeden SessionHost i jeden SystemAgent. Oba pipe'y są gotowe: SessionHost
  `Ready` (protokół 4, 271 procesów), SystemAgent `ReadOnly` (protokół 4,
  316 usług), bez aktywnej sesji i bez uruchomionego przez Dismode procesu
  `PresentMon-2.5.1-x64`.
- Wydanie zawiera `PerformanceOverlayWindow.xbf`, a wdrożony `Dismode.UI.dll`
  ma ten sam SHA-256 co wynik Release. Poprawka timeoutów UI ma kopię:
  `artifacts/Dismode-App.hotfix-backup-20260729-202410`.
- Roblox działał podczas publikacji, lecz przed końcowym odczytem został
  zamknięty poza wykonywanymi przez Codex poleceniami; nie użyto go do smoke
  testu i nie uruchamiano automatycznie Trybu gry.

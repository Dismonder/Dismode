# GameShift Memory Optimizer 0.3.0 — przebudowa UI

Status: implementacja, walidacja i artefakty wydania ukończone; instalacja oraz
kontrola wizualna wymagają udziału użytkownika.

## Zatwierdzony zakres

- Niezależny komponent GPL, bez referencji do zamkniętych bibliotek GameShift.
- Ciemny rozszerzony pasek tytułu, Mica, lokalna ikona, czytelna typografia,
  poprawne stany oraz High Contrast.
- NavigationView: Przegląd, Automatyzacja, Procesy, Historia, Zaawansowane.
- Szkic ustawień z jawnym Zapisz/Odrzuć; odświeżanie i chowanie do trayu
  nie mogą go utracić ani zapisać bez zgody.
- Opcjonalne metryki commit/pagefile; zgodność ze starszym IPC.
- Ustawienia JSON v2 z CompactAlwaysOnTop=false; bez migracji tabel SQLite.
- Osobny mini-panel około 420×220, opcjonalnie zawsze na wierzchu.
- Status co 5 s, lokalny minutowy trend, procesy tylko na aktywnej stronie,
  historia przy wejściu i po operacji.
- Pełna nawigacja od 1100 px, zwinięta od 760 px, pionowy układ poniżej;
  okno normalne minimum 940×640 z ograniczeniem do work area.
- Build/test/format obu rozwiązań, źródła GPL i instalator 0.3.0 z SHA-256.
- Bez sterowania ekranem, uruchamiania instalatora, agresywnych operacji,
  publikacji i commitów; zachować wcześniejsze modyfikacje.

## Zadania i postęp

1. **Metryki i migracja ustawień** — ukończone; mapowanie commit/pagefile,
   zgodność starszego IPC, JSON v1→v2 i niezmieniony schemat SQLite mają testy.
2. **Powłoka i responsywne strony** — ukończona implementacja; ciemny tytuł,
   NavigationView, układy 0/760/1100 px, mini-panel, High Contrast i lokalna ikona.
3. **Model prezentacji** — ukończony; szkic, odświeżanie stron, trend RAM,
   single-flight oraz zachowanie zmian podczas zapisu mają testy jednostkowe.
4. **Walidacja** — ukończona; oba rozwiązania przechodzą build, testy i format.
   Przegląd wykrył i domknął obsługę nieprawidłowych pól liczbowych, czyszczenie
   nieaktualnego błędu IPC, paletę High Contrast oraz wąski układ profili.
5. **Wydanie** — ukończone; odpowiadające źródła GPL, payloady z manifestami
   SHA-256 i instalator 0.3.0 zostały zbudowane oraz zweryfikowane.
6. **Naprawa startu po instalacji** — ukończona; usunięto powodującą fail-fast
   aktywację `AccessibilitySettings`, dodano niewidoczny startup-probe do bramy
   wydania oraz osobny stan usługi działającej bez interfejsu tray.
7. **Naprawa konserwacji instalatora** — ukończona; akcja odinstalowania używa
   wykrytej ścieżki istniejącej instalacji i nie rozwija `{app}` przed inicjalizacją.
8. **Skalowanie 4K/DPI** — ukończone; manifest trayu deklaruje `PerMonitorV2`
   i `longPathAware`, więc Windows nie powinien bitmapowo powiększać interfejsu
   na monitorach 4K. Dodano test kontraktowy i przebudowano payload.
9. **Niezależne sterowanie FPS** — ukończone; ustawienia mają osobne przełączniki
   śledzenia FPS i nakładki. Wyłączenie śledzenia zatrzymuje provider, czyści
   telemetrię i ukrywa HUD, a wyłączenie nakładki nie wyłącza samego pomiaru.
   Stan jest zapisywany w SQLite v11 i przekazywany przez zgodne IPC.
10. **Stabilny układ przy akcjach** — ukończone; komunikaty, paski postępu i
    pasek niezapisanych zmian zachowują miejsce albo nakładają się nad stroną,
    więc kliknięcie optymalizacji, zapisu lub odświeżenia nie powoduje chwilowego
    przeskoku całego interfejsu.

## Decyzje podczas wznowienia

- Kontynuacja w aktualnym checkout zgodnie z poleceniem zachowania zmian;
  brak nowego worktree i commitów.
- Istniejące zmiany nie są kasowane na potrzeby TDD. Nowe regresje otrzymują
  test przed poprawką.
- Przycisk mini-panelu ani przypięcia nie może zapisać szkicu reguł.
  Są to jawne akcje prezentacyjne; zapisują tylko własną preferencję.
- Kontrola wizualna macierzy DPI nie będzie deklarowana bez rzeczywistego
  wykonania; użytkownik nie zezwala na automatyzację pulpitu.

## Wyniki weryfikacji

- `dotnet build` projektu WinUI Release: 0 błędów, 0 ostrzeżeń.
- Testy komponentu Release: 58/58 Core oraz 12/12 Security; łącznie 70 testów
  zaliczonych i 1 kontrolowany test agresywny pominięty bez jawnej flagi VM.
- Testy głównego GameShift: 217/217 (77 Unit, 124 Integration, 13 Recovery,
  3 Security).
- `dotnet format ... --verify-no-changes`: przechodzi dla obu rozwiązań.
- Manifest głównego payloadu: 703/703 pliki zgodne; manifest Memory Optimizer:
  736/736 pliki zgodne; wszystkie sprawdzone własne binaria mają wersję 0.3.0.0.
- Finalny startup-probe payloadu kończy się kodem 0 i nie tworzy nowego
  zdarzenia awarii aplikacji.
- Instalator: `artifacts/installer/GameShift-Setup-0.3.0-win-x64.exe`, SHA-256
  `0928FACF8DE43BBAC3B6AC3C96F4675865E54005D2C7B5EFF07A09FBD051DDCA`
  (163 143 906 B).
- Odpowiadające źródła GPL: SHA-256
  `22B799207B2CCCC4BA6F85953191AD033D660D7735DEBD86FB7484159EA36EED`.
- Manifest preview `artifacts/update-service-0.3.0` ma wersję 0.3.0,
  minimum `0.1.1`, osiem chunków i zgodny SHA-256 instalatora; publikator
  jest uruchamiany automatycznie przez `Build-Installer.ps1`.
- Sprawdzone binaria payloadów mają wersję `0.3.0.0`; podpis Authenticode
  instalatora pozostaje `NotSigned` do czasu dostarczenia certyfikatu wydawcy.
- Kontrola wizualna i instalacja pozostają po stronie użytkownika; zgodnie z
  poleceniem nie uruchamiano aplikacji ani automatyzacji pulpitu.

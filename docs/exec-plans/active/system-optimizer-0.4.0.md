# Plan wykonawczy: GameShift System Optimizer 0.4.0

Status: implementacja i lokalna regresja hosta zakończone; lokalny staging
0.4.0 jest zbudowany i zweryfikowany, a macierz VM oraz produkcyjny podpis
pozostają wymagane przed dystrybucją

Utworzono: 2026-08-30
Źródło wymagań: zaakceptowany plan użytkownika „GameShift System Optimizer
0.4.0 — pomiarowy optimizer A/B”

## Cel

Dostarczyć osobny interfejs `GameShift.SystemOptimizer.exe` i usługę Windows
`GameShiftSystemAgent`, które wykonują wyłącznie katalogowane, mierzone metodą
A/B i odwracalne zmiany. Brak zweryfikowanego adaptera oznacza `Unsupported`,
a Hard Safety Policy nie może zostać ominięta zgodą użytkownika.

## Zrealizowano

- wersja produktu 0.4.0 z jednego źródła oraz protokół IPC v6 bez zmiany
  numerów istniejących enumów;
- osobne kontrakty katalogu, fingerprintu, eksperymentu, przechwycenia,
  wyniku, profili i recovery;
- usługa LocalSystem z delayed-auto, systemowy named pipe, limit 1 MiB,
  timestamp, replay, idempotency key oraz weryfikacja rzeczywistego SID, PID,
  ścieżki i podpisu klienta;
- fail-closed: brak produkcyjnego certyfikatu daje wyłącznie odczyt;
- osobna SQLite v1 i append-only machine journal, single-flight, checkpointy,
  niezależny readback oraz trójstronny restore;
- działające adaptery procesu gry (`AboveNormal`, wyłączenie Power
  Throttling) i stały adapter hibernacji; nieweryfikowalne kategorie pozostają
  widoczne jako `Unsupported`;
- trwała lista Hard Safety Policy dla zabezpieczeń, anti-cheat, WHEA,
  watchdogów i wszystkich modyfikacji BCD/timerów;
- surowe frametimes PresentMon, GPU busy, RAM systemowy, wymagania jakości,
  1%/0,1% low, hitch rate, stabilność bloków i adaptacyjna druga para;
- aktywacja bezpiecznego profilu po uruchomieniu zweryfikowanej gry i restore
  po jej zakończeniu lub po restarcie usługi;
- osobny WinUI 3 z ciemnym rozszerzonym paskiem, Mica, High Contrast,
  responsywną nawigacją, ośmioma ekranami, stałymi powierzchniami postępu,
  metrykami A/B oraz listą profili właściciela;
- per-item Danger consent `ROZUMIEM RYZYKO` i osobna promocja zwycięzcy jako
  profil globalny;
- integracja karty w głównym UI, bezpieczny update/uninstall restore, skrypty
  usługi oraz instalator z obowiązkową bramą produkcyjnego podpisu;
- kanał preview 0.4.0 z minimalną bezpośrednią aktualizacją z 0.3.0.

## Świadomie niewykonywane na hoście

- agresywne lub globalne mutacje Windows;
- rzeczywisty test hibernacji;
- instalacja, restart lub usunięcie usługi;
- uruchamianie UI, gry, instalatora albo automatyzacja pulpitu;
- testy restartowe i crash-injection wymagające jednorazowej VM;
- deployment Cloudflare.

## Bramy pozostałe przed wydaniem

Aktualny stan 2026-09-05 po zleceniu publikacji dla innych użytkowników:

- Produkcyjny certyfikat Authenticode nie jest skonfigurowany. Poprzedni
  lokalny instalator ma obecnie status UnknownError (niezaufany root), więc
  wcześniejszy wpis o Valid nie potwierdza gotowości bieżącej paczki.
- Publikator nie tworzy już zastępczego klucza podpisu aktualizacji. Przed
  zapisem pakietu wymaga oryginalnego klucza zgodnego z zaufaniem klientów,
  a gotowy manifest weryfikuje kluczem przypiętym w produkcie.
- Sprawdzenie tożsamości dostępnego klucza aktualizacji przeszło. Build
  publikatora, 15 testów dotyczących zaufania/manifestu/wydania i kontrola
  formatowania przeszły. Klucz aktualizacji nie zastępuje certyfikatu EXE.
- Użytkownik zlecił publikację, ale nie wykonano deploymentu: poniższe bramy
  podpisu produkcyjnego oraz VM nadal nie są spełnione.
- Serwer aktualizacji: poprawiono niecache'owalne błędy pobierania oraz HEAD
  dla odpowiedzi sukcesu i błędów. 18/18 testów (w tym rzeczywisty lokalny
  binding statycznego manifestu), typecheck kodu i testów oraz dry-run
  pakowania przeszły. Nie wdrażano tych zmian publicznie.
- Na hoście nie znaleziono dostępnych poleceń/usług Hyper-V, VirtualBox ani
  VMware do przeprowadzenia macierzy testów VM. Nie instalowano hypervisora
  i nie zmieniano konfiguracji systemu użytkownika.

1. Produkcyjny certyfikat Authenticode oraz sprawdzenie allow-listy podpisu.
2. Macierz VM Windows 11 23H2/24H2: instalacja, update 0.3.0→0.4.0,
   restart usługi/systemu, recovery po każdym checkpointcie i uninstall.
3. Ręczna kontrola wizualna 1080p/4K, DPI 100–200%, High Contrast i Narrator.
4. Potwierdzenie aktualizacji i bezpiecznej deinstalacji na VM; lokalny
   podpisany staging, SHA-256 i manifest preview są już zweryfikowane.

## Walidacja lokalna 2026-08-30

- `GameShift.sln`: restore i Debug build zakończone bez ostrzeżeń; 285/285
  testy przeszły, a `dotnet format --verify-no-changes` jest czysty;
- `GameShift.MemoryOptimizer.sln`: restore i Release build zakończone bez
  ostrzeżeń; 70 testów przeszło, a jeden agresywny test VM został prawidłowo
  pominięty bez jawnej flagi; formatowanie jest czyste;
- na hoście nie uruchomiono UI, instalatora, gry ani żadnej mutacji Windows.
- Worker aktualizacji: 5/5 testów, typecheck, ponowne generowanie typów i
  `wrangler deploy --dry-run` przeszły bez deploymentu;
- Inno Setup 6.7.3 skompilował skrypt 0.4.0 z tymczasowym minimalnym
  payloadem; Windows PowerShell 5.1 parsuje wszystkie 13 skryptów wydania, a
  brak produkcyjnego thumbprintu prawidłowo zatrzymuje `Build-Installer.ps1`.

## Walidacja Windows po handoffie Linux 2026-09-05

- Windows 11 x64 build `28120`: restore i build Debug `GameShift.sln` oraz
  restore i build Release rozwiązania Memory Optimizer zakończyły się bez
  ostrzeżeń i błędów.
- Bezpieczna regresja Release GameShift przeszła: 134 Unit, 13 Recovery,
  140 read-only Integration i 4 Security. Jeden test symlinków pominięto z
  powodu braku funkcji na hoście. Memory Optimizer przeszedł 58 Core i 12
  Security; agresywny test VM pozostał pominięty bez jawnej flagi.
- Dedykowany zestaw OptiScaler na Windows przeszedł 17/17 testów Unit oraz
  21/22 testów Integration; jedyny skip to brak obsługi symlinków na hoście.
- `dotnet format --verify-no-changes` jest czysty dla obu rozwiązań.
  `SystemMemoryCleanerTests` wymagają teraz jawnego
  `GAMESHIFT_ALLOW_AGGRESSIVE_MEMORY_TESTS=1`, więc zwykła regresja nie
  zmienia globalnego cache systemu.
- Read-only smoke: recovery journal czysty (1127 rekordów), PresentMon 2.5.1
  `Ready`, `GameShift.SystemAgent.exe --diagnostics` zwraca IPC v6.
- Finalny staging lokalny: instalator 0.4.0 ma 223 888 088 B i SHA-256
  `26C6A83C5D870E12F8CFC272AA5BD528F88F10D5F71D51427435914475C70369`.
  Authenticode `Valid`; 24 własne binaria GameShift i sześć binariów Memory
  Optimizer ma wersję `0.4.0.0` i zgodny podpis. Manifest preview `0.4.0`
  (`minimumSupportedVersion=0.3.0`) zawiera 11 chunków zgodnych z hashem
  instalatora.
- GPL release gate przeszedł; NOTICE i odpowiadające źródło są 0.4.0, a
  archiwum źródłowe ma SHA-256
  `7FFFDF98286946C588183251A5F3A28EB058A0196426B2347A746AF36032B8BF`.
  Staging podpisano lokalnym certyfikatem deweloperskim `CN=GameShift
  Development`; podpis produkcyjny i testy VM pozostają bramami dystrybucji.
- Nie uruchamiano instalatora, nie instalowano SystemAgent, nie wykonywano
  restartu, gry ani agresywnej mutacji Windows.

## Kryterium zakończenia

Wydanie może zostać oznaczone jako gotowe dopiero po zielonej regresji,
ważnym podpisie produkcyjnym, pełnym recovery na VM oraz poprawnym update i
uninstall. Brak którejkolwiek bramy jest blokadą wydania, nie ostrzeżeniem.

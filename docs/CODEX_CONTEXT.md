# Dismode — kontekst roboczy repozytorium

Ostatnia aktualizacja: 2026-10-01 (wydanie 0.8.0)

## Zasady prowadzenia sesji (od 2026-10-01)

- Ten plik jest kontekstem dla następnej sesji: nowa sesja zamiast kompresji
  kontekstu. Każda sesja dopisuje tu wpis do change logu i stan prac.
- Większe pliki do przeczytania zleca się Antigravity CLI (Gemini Flash,
  `agent_call` model `flash`, profil `find`/`shell`), nie czyta się ich
  w kontekście sesji Claude.
- Recenzja i cięższa praca wykonawcza: Codex CLI WYŁĄCZNIE z modelem
  w wersji 6.1 (`codex exec -m gpt-6.1-sol`, effort `xhigh`); `agent_call`
  z modelem `sol`/`astra` mapuje na stare gpt-6-* i nie jest używany
  (decyzja Damiana 2026-10-01).

## Change log — gałąź `rename-dismode` (stan 2026-10-01, niezacommitowane)

- `.gitattributes`: `* text=auto eol=lf`, binaria oznaczone; `dotnet format`
  znów zielony.
- `LegacyStorageMigration`: wynik `LegacyStorageMigrationResult`
  (`Problems`, `BlocksStartup`), mutex `Local\Dismode.StorageMigration.*`,
  scalanie katalogów rekurencyjnie, identyczne pliki usuwane, kolizje
  odkładane pod `.gameshift`, baza `gameshift-user.db*` przenoszona zestawem
  (db, -wal, -shm) lub odkładana jako `.unmerged`, ponawianie `Move`.
- `LegacyProductProcesses` (nowy plik): wykrycie `GameShift.UI` /
  `GameShift.SessionHost` w bieżącej sesji.
- `StartupPolicy`: kody 6 (`LegacyProductRunning`) i 7
  (`LegacyDataUnavailable`); `StartupGate.RefuseWhileLegacyProductRuns`,
  `StartupGate.MigrateLegacyData`; `App.xaml.cs` używa obu.
- `SessionHost/Program.cs`: zwraca 9 przy działającym GameShift lub
  zablokowanej migracji; `Dismode.iss` tłumaczy `ResultCode = 9`.
- `SqliteUserDataStore`: schemat 15 (`ActionMode IN (1..5)` w regułach gry,
  sieroty odfiltrowane), migracje ponawiane po wyścigu UI/host; usunięte
  `Cache=Shared` (także w `SqliteSystemOptimizerStore`).
- `AppendOnlyRecoveryJournal`: zweryfikowane wpisy trzymane w pamięci
  po przejęciu writera.
- `LocalGameSessionOrchestrator`: affinity gry dziennikowane i przywracane
  (`GameAffinityRecoveryMetadata`, `RestoreGameAffinityAsync`), koniec sesji
  nie wcześniej niż start (`ResolveSessionEnd`).
- `WindowsSystemTweakRuntime`: ziarno id `GameShift.SystemOptimizer.v1`
  zachowane celowo (zgodność dziennika maszyny).
- `BackgroundApplicationGuard`, `ProcessClassificationService`,
  `EtwSessionCleanup`, `PresentMonFrameRateProvider`: stare nazwy GameShift
  chronione / sprzątane.
- `TrustedSignerConfiguration`: plik honorowany tylko gdy właścicielem jest
  Administrators lub SYSTEM; `Install-SystemAgent.ps1` ustawia właściciela
  i DACL `ProgramData\Dismode` (Shared do zapisu dla Users).
- Skrypty PS1 instalatora dostały BOM UTF-8 (polskie komunikaty w PS 5.1).
- Testy: nowe w `UserDataStoreTests`, `AppendOnlyRecoveryJournalTests`,
  `LegacyStorageMigrationTests`, `StartupPolicyTests`,
  `SystemOptimizerReleaseContractTests`.

Weryfikacja 2026-10-01: build 0/0, testy 565 zaliczone (7 pominięte),
`dotnet format --verify-no-changes` czysto, `git diff --check` czysto.

Recenzja Codex (gpt-6-sol, 2026-10-01) diffu pod kątem wyścigu migracji,
schematu 15, recovery affinity i ACL `ProgramData\Dismode`:
- naprawione: `LegacyStorageMigration` przenosi zestaw bazy w kolejności
  `-shm`, `-wal`, baza (nazwa bazy jako ostatni krok), a pozostawiony legacy
  `-wal` w którymkolwiek katalogu daje `BlocksStartup` (wcześniej baza bez
  dziennika WAL otwierała się bez zatwierdzonych wierszy);
  `Install-SystemAgent.ps1` robi `icacls /reset` korzenia przed
  `/inheritance:r`, bo jawny wpis CREATOR OWNER użytkownika, który utworzył
  katalog wcześniej, przetrwałby zdjęcie dziedziczenia.
- świadomie bez zmian: po 30 s bez mutexu migracja idzie dalej (przeplot
  dwóch procesów daje tylko wpisy w `Problems`, `TryMove` nie gubi plików);
  `SQLITE_BUSY` przy migracji schematu dłuższej niż timeout połączenia
  (migracja 14→15 trwa ułamek sekundy); okno awarii hosta między
  nałożeniem twardej maski gry a checkpointem `ProcessesApplied` (gra
  zostaje na masce P-core do wyjścia; checkpoint przed akcją wymagałby
  recovery dla akcji bez wpisów w dzienniku).
- schemat 15 i round-trip `GameAffinityRecoveryMetadata`: bez uwag.

Weryfikacja po poprawkach 2026-10-01: build 0/0, testy 565 zaliczone
(7 pominięte), `dotnet format --verify-no-changes` czysto,
`git diff --check` czysto. PR #5 (`rename-dismode` → `main`) już istniał,
więc jego opis został uzupełniony zamiast tworzenia nowego.

Druga recenzja, Codex 6.1 (`gpt-6.1-sol`, xhigh, 2026-10-01), commitu
555d703: werdykt „niegotowe", naprawione w kolejnym commicie:
- `LegacyStorageMigration`: bez mutexu (timeout 30 s albo brak uchwytu)
  migracja zwraca `BlocksStartup`, gdy są dane do przeniesienia; zestaw bazy
  nie jest rozdzielany przy ponowieniu — `RenameLegacyDatabase` przerywa po
  pierwszym nieudanym przeniesieniu sidecara (baza zostaje pod starą nazwą,
  także w wariancie `.unmerged`), a sidecar pozostawiony w katalogu legacy
  wstrzymuje rename bazy już przeniesionej (`SidecarLeftBehind`);
  `LegacyMachineDataDirectory` wystawione publicznie. Test
  `ALockedJournalKeepsTheDatabaseSetTogetherAcrossRestarts` (4 warianty).
- `LocalGameSessionOrchestrator`: identyfikatory twardej maski affinity gry
  trafiają do checkpointu `GameLaunched` PRZED `ExecuteAsync`
  (`recordBeforeApply`); recovery bez wpisów akcji zwraca
  `MissingPreparation` → 0/0/0, więc wcześniejszy zapis nic nie psuje.
- `StartupGate`: `StartElevated` zwraca `Process`, `WaitForPipe` kończy się,
  gdy host wyjdzie; kod wyjścia hosta ≠ 0 bez rury → `StartupPolicy.HostRefused`
  (9 → kod 7 z komunikatem o GameShift/migracji, inne → kod 5). Stała
  `StartupPolicy.HostExitRefused = 9` użyta w `SessionHost/Program.cs`;
  komunikat kodu 7 mówi też o niepełnym przeniesieniu. Test w
  `StartupPolicyTests`.
- `SystemAgent`: nowy `Security/MachineDataAccessControl.cs` — po zapasowej
  migracji danych maszyny (gdy katalog `ProgramData\GameShift` istniał)
  dzieci `ProgramData\Dismode` tracą jawne ACE i dostają właściciela
  Administrators (katalog `Shared` zachowuje swój wpis Users M). Semantyka
  (`RemoveAccessRuleAll` + `SetAccessRuleProtection(false,false)` na
  istniejącym `FileSystemSecurity`; świeży obiekt zapisałby null DACL)
  sprawdzona w PowerShellu na katalogu tymczasowym.
- Świadomie bez zmian: `SQLITE_BUSY` przy migracji schematu dłuższej niż
  5 s timeoutu (transakcje chronią dane, drugi start wystarcza).

Weryfikacja 2026-10-01 (po drugiej recenzji): build 0/0, testy 570
zaliczone (7 pominięte; +4 Recovery, +1 Unit), `dotnet format
--verify-no-changes` czysto, `git diff --check` czysto.

Wydanie 0.8.0 (2026-10-01): `<Version>` w `Directory.Build.props` podbity
do 0.8.0 w tym samym commicie, PR #5 scalony do `main`.

Walidacja wydania 0.8.0 (2026-10-01, `main` = 7c202e9):
`tools/Build-LocalRelease.ps1 -SkipTests -AllowTestCodeSigningCertificate`
z certyfikatem testowym „GameShift Development" (B78D…63A5) przeszła
do końca: `artifacts/Dismode-App` (206 MB), `Dismode.exe`, `Dismode.UI.exe`,
`Dismode.SessionHost.exe`, `Dismode.SystemAgent.exe` w wersji
`0.8.0-gaming-edition+7c202e9…`, podpisy Authenticode `Valid`; sparse
package `ShellIntegration/Dismode.Sparse.msix` podpisany nowym certyfikatem
deweloperskim (E853…BEA3, status `UnknownError` = brak zaufanego łańcucha,
oczekiwane poza instalacją produkcyjną). `Build-Installer.ps1` wymaga
produkcyjnego odcisku (`DISMODE_RELEASE_SIGNING_THUMBPRINT`), więc paczka
instalatora 0.8.0 czeka na certyfikat.

PR #4 (`fix/canonical-executable-paths`, gra rozpoznawana po pliku, nie po
tekście ścieżki) scalony z `main` po zmianie nazwy: cztery nowe pliki
przeniesione do `src/Dismode.Windows/Processes` i
`tests/Dismode.IntegrationTests/Processes`, przestrzenie nazw i nazwy
produktu w nich zmienione na Dismode; pozostałe pliki scaliły się
automatycznie. Weryfikacja: build 0/0, testy 582 zaliczone (7 pominięte;
+12 Integration z PR), `dotnet format --verify-no-changes` czysto.

Recenzja Codex 6.1 PR #4 (2026-10-01) i poprawki:
- naprawione: junction na samym katalogu gry (`steamapps\common\Gra` →
  `D:\Gra`) gubił znacznik biblioteki, więc root wyprowadzony z rozwiązanej
  ścieżki EXE obejmował tylko podkatalog `bin` — teraz root z biblioteki jest
  rozwiązywany osobno (`ResolvedGamePaths.InstallationRoot`); rozwiązanie
  ścieżki gry i rootu trzymane w pamięci podręcznej 30 s / 16 wpisów
  (`ResolveGamePaths`), zamiast otwierać plik gry przy każdym nietrafionym
  procesie skanu (na odłączonym udziale każde otwarcie czeka sekundy).
  Test `AHelperBesideTheExecutableDirectoryIsRecognisedThroughAGameDirectoryJunction`.
- świadomie bez zmian (zachowanie identyczne jak przed PR): nierozstrzygnięta
  tożsamość (brak dostępu, wolumen bez litery) = brak dopasowania; hard-linki
  do tego samego EXE w różnych katalogach; katalogi NTFS z rozróżnianiem
  wielkości liter; alias-symlink o innej nazwie EXE odpada na filtrze nazwy
  procesu.

Weryfikacja PR #4 po poprawkach: build 0/0, testy 583 zaliczone
(7 pominięte), `dotnet format --verify-no-changes` czysto.

Żywy test migracji (2026-10-01, po odinstalowaniu GameShift przez Damiana):
- `Build-Installer.ps1` z certyfikatem testowym przeszedł do
  `artifacts/installer/Dismode-Setup-0.8.0-win-x64.exe` (224 MB, podpis
  `Valid`, SHA-256 obok); ostatni krok (staging manifestu aktualizacji przez
  `Dismode.UpdatePublisher`) kończy się kodem 1, bo klucz podpisu
  aktualizacji jest tylko na maszynie wydawniczej — do instalacji lokalnej
  niepotrzebny. Wymagało to ręcznego przywrócenia zaufania dla certyfikatu
  testowego (deinstalator GameShift usunął go z `TrustedPeople`).
- Instalacja `/SILENT` kod 0; usługi `DismodeSystemAgent` i
  `DismodeMemoryService` działają; `ProgramData\Dismode` ma DACL Users RX /
  Administrators F / SYSTEM F, `Shared` dodatkowo Users M; dane maszyny
  przeniesione. Baza Memory Optimizera z GameShift była jeszcze zapisywana
  o 11:30 (po deinstalacji), więc faza Prepare instalatora nie mogła jej
  przenieść; nowa usługa założyła własną, a agent odłożył starą jako
  `memory-optimizer.gameshift.db` (bez utraty, ale bez przejęcia).
- Pierwszy start z launchera (host + UI równocześnie): baza i dziennik
  recovery przeszły (schemat 15), ale w `LocalAppData\GameShift\Artwork`
  zostało 197 z 213 bitmap, bez żadnego wpisu w `ui-startup-errors.log`.
  Sonda (konsola z `Dismode.Data.dll`) przeniosła resztę w 308 ms, drugi
  start UI z syntetycznym katalogiem legacy (201 plików) przeniósł wszystko.
  Przyczyna wskazana w kodzie: mutex `Local\Dismode.StorageMigration.*`
  założony przez podniesiony host ma DACL tylko dla Administrators/SYSTEM,
  więc niepodniesione UI nie może go otworzyć (`TryOpenMutex` → null) —
  blokada nie serializowała obu procesów.
- Poprawka: `LegacyStorageMigration` zamiast mutexu używa pliku blokady
  `<katalog>.migration.lock` obok katalogu danych (`FileShare.None`,
  `DeleteOnClose`, ponawianie do 30 s; `Migrate(..., lockTimeout)` dla
  testów). Test `AMigrationHeldByAnotherComponentRefusesTheStartInsteadOfInterleaving`.
  Weryfikacja: build 0/0, testy 585 zaliczone (6 pominięte), format czysto.

Strona WWW (2026-10-01): `infrastructure/website/public` przebudowana przez
Codex 6.1 (limit użycia skończył mu się przed raportem; pliki kompletne):
8 sekcji całego projektu (moduły, sesja w 4 fazach, bezpieczeństwo,
instalacja i migracja z GameShift, nowości 0.8.0, FAQ, licencje), bez
inline JS/CSS (CSP z `_headers` zachowana), przełącznik efektów i
`prefers-reduced-motion`, 45 KB HTML+CSS+JS; skrypt kontrolny
`tools/check-site.mjs` (linki, kotwice, nagłówki, CSP, rozmiar) zielony;
sprawdzona w podglądzie na desktopie, 375 px i 404. Dawny shader WebGL
i animacje przewijania zostały usunięte na rzecz czytelności. Wdrożona na
`dismode-site-dev`.

Przenośność na inne komputery (2026-10-01, audyt `dumpbin /dependents`
payloadu): `Dismode.ShellExtension.dll` (menu kontekstowe Windows 11,
sparse package) wymagał `MSVCP140`/`VCRUNTIME140`/`VCRUNTIME140_1`, czyli
redystrybucji VC++, której instalator nie dokłada — na czystym systemie
moduł powłoki nie ładowałby się. Poprawka: `RuntimeLibrary=MultiThreaded`
(Release) / `MultiThreadedDebug` (Debug) w vcxproj; po przebudowie zależy
tylko od SHELL32/ole32/SHLWAPI/KERNEL32. Pozostałe binaria (WindowsAppRuntime,
Microsoft.ui.xaml, coreclr, Dismode.UI.exe, SystemAgent, PresentMon) bez
zależności od CRT; payload self-contained (.NET + Windows App Runtime).
Ograniczenie buildu z certyfikatem testowym na innych maszynach:
`AuthenticodeSignatureVerifier` używa WinVerifyTrust, więc niezaufany
samopodpisany łańcuch daje agentowi tryb tylko do odczytu (System
Optimizer nie zastosuje zmian) i ostrzeżenie SmartScreen; pełna
funkcjonalność wymaga produkcyjnego certyfikatu
(`DISMODE_RELEASE_SIGNING_THUMBPRINT`).

Korekta po dalszej diagnozie (2026-10-01, popołudnie): powłoki narzędziowe
sesji Claude (aplikacja MSIX) widzą `%LOCALAPPDATA%` przez nakładkę
`Packages\Claude_…\LocalCache\Local` (odczyty przechodzą do prawdziwych
plików, zapisy i przeniesienia zostają w nakładce), a procesy potomne powłoki
dziedziczą ją; procesy z launchera (runas/explorer) widzą prawdziwy profil.
Wszystkie testy „syntetyczny katalog legacy + start UI/hosta” po 11:56
były skażone, a usunięcie nakładki skasowało 197 bitmap cache grafik
(`LocalGameArtworkResolver` odbudowuje je z ikon EXE i bibliotek Steam/Epic).
Wiarygodne fakty: pierwszy start z launchera o 11:51 (build z mutexem)
przeniósł bazę, dziennik i 16 grafik (obserwacja z 11:52 sprzed pierwszego
zapisu do nakładki); po aktualizacji instalacji buildem z blokadą plikową
prawdziwy katalog `GameShift` zniknął, a nowy ślad `migration.log` z
procesów launchera (`exists=False leftBehind=0 problems=0`) potwierdza
brak pozostałości. `migration.log` (jedna linia na start, limit 256 KB)
zostaje w produkcie jako pierwszy wiarygodny obraz tego, co widzi
prawdziwy proces. Szczegóły pułapki: pamięć sesji
`msix-virtualized-appdata-pitfall`.

Finalna instalacja 0.8.0 (2026-10-02 07:58, build z `main` 37ab7a9):
`Build-Installer.ps1` pod PowerShell 7 (pod Windows PowerShell 5.1 pada na
`IsPathFullyQualified`), instalacja `/SILENT` kod 0, usługi
`DismodeSystemAgent` i `DismodeMemoryService` działają, zainstalowany
`Dismode.ShellExtension.dll` zależy tylko od SHELL32/ole32/SHLWAPI/KERNEL32,
procesy z launchera zapisały w `migration.log` `exists=False leftBehind=0
problems=0`. Instalator: `artifacts/installer/Dismode-Setup-0.8.0-win-x64.exe`
(certyfikat testowy; staging manifestu aktualizacji wymaga maszyny
wydawniczej).

Wydanie na GitHubie (2026-10-02): tag `v0.8.0` = `main` f3bb7f2, release
(prerelease, tytuł „Dismode 0.8.0 Gaming Edition (wydanie rozwojowe, podpis
testowy)”) z `Dismode-Setup-0.8.0-win-x64.exe` zbudowanym z wyewidencjonowanego
tagu (`ProductVersion 0.8.0-gaming-edition+f3bb7f2…`, SHA-256
`61011EA2…3851`, plik `.sha256` obok) i notatką z change logu; przycisk
„Pobierz” na stronie prowadzi do `releases/latest`. Build-Installer stempluje
`InformationalVersion` bieżącym HEAD, więc wydanie buduje się z checkoutu
tagu, nie z gałęzi roboczej.

Następne kroki: produkcyjny certyfikat (`DISMODE_RELEASE_SIGNING_THUMBPRINT`)
i paczka dla update-service; pierwsza instalacja Dismode obok
zainstalowanego GameShift to pierwszy żywy test migracji (w tym ścieżki
kodu 9 → komunikat UI); `wrangler deploy` nowego adresu update-service;
instalator 0.8.0 po podaniu produkcyjnego certyfikatu.

### Memory Optimizer — bieżąca poprawka panelu tray (2026-09-05)

- Nowy panel 400×548 DIP: fizyczny RAM, dostępna pamięć, stan automatu,
  osobne zamknięcie interfejsu i wyłączenie komponentu z potwierdzeniem.
- Pauza i optymalizacja pozostawiają menu otwarte; stan offline blokuje akcje.
  Układ ma jawne kolumny, stałe powierzchnie statusu i ciemną ramkę DWM.
- Zweryfikowano kompilację Release, 75 testów komponentu (1 agresywny test
  pominięty zgodnie z bramą VM) oraz 20 testów integracji/wydania.
- Paczka `artifacts/Dismode-MemoryOptimizer-tray-preview` została zbudowana
  z odpowiadającymi źródłami GPL i SHA-256. Przeszła `dotnet format`, kontrolę
  GPL oraz ukryty test startu i pomiaru panelu. Kontrola wizualna na pulpicie
  nie została wykonana (zgodnie z prośbą użytkownika).
- Reinstalacja nie została wykonana: certyfikat wydania nie jest skonfigurowany,
  a poprzedni instalator ma podpis UnknownError (niezaufany root). Nie omijać
  walidacji podpisu. Paczka developerska nie jest zatwierdzonym instalatorem.

## Źródła prawdy

- Specyfikacja produktu: `C:\Users\Damia\.codex\attachments\0f65d4a6-1fd6-4074-8123-d7d75a98d69b\pasted-text-1.txt`.
- Pomocniczy plan naprawy i rozwoju:
  `C:\Users\Damia\.codex\attachments\74706e63-bb89-409f-89d3-98f82d6a7260\pasted-text.txt`.
  Rozmowa ChatGPT „Ocena Dismode 0.1.0” jest wejściowym materiałem
  analitycznym, nie źródłem prawdy o aktualnym kodzie; twierdzenia wpływające
  na implementację trzeba potwierdzić w repozytorium.
- Instrukcje pracy: `AGENTS.md`.
- Aktywne plany: `docs/exec-plans/active/dismode-mvp.md`,
  `docs/exec-plans/active/system-optimizer-0.4.0.md` oraz
  `docs/exec-plans/active/launch-latency-bento-ui.md`. Plan Safety
  Release znajduje się w
  `docs/exec-plans/completed/safety-release-0.1.1.md`. Zakończone plany:
  `docs/exec-plans/completed/fps-overlay-v2.md`,
  `docs/exec-plans/completed/aggressive-mode-overlay.md`,
  `docs/exec-plans/completed/presentmon-migration.md`,
  `docs/exec-plans/completed/overlay-settings.md` oraz
  `docs/exec-plans/completed/session-performance-history.md` i
  `docs/exec-plans/completed/one-click-saved-optimization.md`, a także
  `docs/exec-plans/completed/installer-exe.md`.

## Stan repozytorium

- Katalog roboczy `C:\Users\Damia\Documents\Win-Optymalizer` był pusty na początku audytu.
- 2026-07-28 użytkownik wyraźnie zatwierdził utworzenie programu od zera w tym katalogu.
- Repozytorium Git zostało zainicjalizowane na gałęzi `main`; nie utworzono jeszcze pierwszego commita.
- Istnieje rozwiązanie `Dismode.sln` z szesnastoma projektami.
- Jedynym źródłem wersji produktu jest `Directory.Build.props`; bieżąca
  wersja to `0.4.0`, protokół IPC to v6, a nazwa produktu jest wyprowadzana z
  metadanych assembly.
- `Dismode.SystemAgent` jest usługą Windows LocalSystem z opóźnionym startem
  i jedynym writerem `system-optimizer.db` oraz append-only
  `machine-recovery.jsonl`. Mutacje wymagają rzeczywistego SID/PID klienta,
  ścieżki pod katalogiem instalacji i zaufanego podpisu Authenticode; bez
  certyfikatu klient działa tylko do odczytu.
- Osobny `Dismode.SystemOptimizer.exe` ma osiem ekranów, katalog
  Supported/Unsupported/Blocked, surowy pomiar PresentMon A/B, profile per gra
  i globalne, historię oraz jawny restore. Hard Safety Policy bezwarunkowo
  blokuje Defender, firewall, Windows Update, BitLocker, anti-cheat, Secure
  Boot, HVCI, mitigacje, WHEA, watchdog i BCD.
- Biblioteka gier ma menedżer OptiScaler per gra z wyborem konkretnej wersji
  w kanałach: oficjalny Stable (`optiscaler/OptiScaler`), społecznościowy i
  nieoficjalny Beta (`Optiscaler-Client/Optiscaler-Betas`) oraz oficjalny
  codzienny Nightly (`optiscaler/OptiScaler-nightly`). Pobieranie jest
  ograniczone do przypiętego repozytorium danego kanału, rozmiaru i SHA-256.
  Beta/Nightly wymagają osobnego potwierdzenia ryzyka. Menedżer wybiera
  właściwy plik Unreal Shipping EXE, a instalację oraz usuwanie wykonuje
  transakcyjnie z kopią kolidujących plików. Instalacja jest blokowana dla
  uruchomionej gry, wykrytego anti-cheat/Roblox i bez jawnego potwierdzenia
  użycia offline lub single-player.
- Bramy A–E (fundament, bezpieczeństwo, recovery, read-only IPC i kontrolowane akcje procesów) są ukończone i zweryfikowane.
- SessionHost udostępnia rzeczywiste, journalowane akcje procesów użytkownika:
  graceful close/restart, odwracalne `BelowNormal`, priorytet gry
  `AboveNormal/High`, monitoring potomków launchera, łagodne zamknięcie
  aplikacji tła oraz jawne, natychmiastowe zakończenie ponownie
  zweryfikowanego procesu okna aktywnej gry.
  Telemetria FPS pochodzi wyłącznie z przypiętego PresentMon 2.5.1 i
  rzeczywistych zdarzeń ETW. Prawidłowe próbki są agregowane w wynik sesji i
  zapisywane w historii; brak próbek pozostaje jawnym brakiem danych. Mutacje
  usług i planu zasilania pozostają zablokowane.
- Safety Release ustawia `Normal` jako priorytet domyślny. Szybki Play nie
  stosuje zapisanego `AboveNormal`/`High`, a nowi kandydaci procesów tła nie
  są zaznaczani automatycznie. Ręczne priorytety eksperymentalne nadal są
  rzeczywistymi, journalowanymi i odwracalnymi zmianami Windows.
- Pipeline Play działa launch-first: po ponownej walidacji i trwałym
  checkpointcie uruchamia grę, a dopiero potem stosuje sekwencyjnie priorytet
  gry i zatwierdzone działania procesów tła. Dzięki temu do 10 sekund
  oczekiwania na łagodne zamknięcie pojedynczej aplikacji nie blokuje już
  utworzenia procesu gry.

## Środowisko deweloperskie

- System: Windows x64, raportowana wersja `10.0.28120`.
- .NET SDK: `10.0.302` oraz `8.0.420`.
- MSBuild z SDK .NET: `18.6.11`.
- Visual Studio Community 2026: `18.7.4`, instalacja kompletna i uruchamialna.
- Inno Setup `6.7.3` zainstalowany w zakresie użytkownika wyłącznie jako
  kompilator instalatora.
- Windows SDK: `10.0.26100.0` (obok starszych SDK).
- Szablony projektu WinUI 3 / Windows App SDK są zainstalowane w Visual Studio.
- WinUI 3 nie było widoczne jako szablon w `dotnet new list`; projekt został przygotowany zgodnie z aktualnym szablonem Visual Studio i dokumentacją Microsoft.
- Stabilny Windows App SDK: `2.3.1`.
- TFM Windows: `net10.0-windows10.0.26100.0`.
- Minimalny obsługiwany system: Windows 11 23H2 build 22631
  (`10.0.22631.0`).

## Stos

- C# i .NET 10.
- WinUI 3, XAML i stabilny Windows App SDK `2.3.1`.
- Windows 11 x64, minimalnie 23H2.
- Docelowy sprzęt produktu: komputer stacjonarny x64; profile baterii laptopa
  nie należą do bieżącego zakresu.
- SQLite dla lokalnych baz użytkownika i maszyny.
- Przypięty PresentMon 2.5.1 (MIT) jako rzeczywiste źródło FPS.
- Bezpieczne API Win32 przez wydzieloną warstwę interoperacyjności.
- Bieżące wydanie dystrybucyjne używa Inno Setup 6 i pojedynczego,
  self-contained instalatora EXE; docelowy podpisany MSI pozostaje możliwym
  późniejszym kanałem korporacyjnym.
- Brak Electron, WebView, lokalnego HTTP, chmury oraz sterownika kernel-mode.

## Mapa modułów

- `src/Dismode.Launcher`: `Dismode.exe` z manifestem `requireAdministrator`
  (od 0.3.0: jeden monit UAC dla hosta); uruchamia lub odnajduje SessionHost
  i UI, lecz nie uruchamia SystemAgent, który jest zarządzany przez SCM jako
  usługa. UI startuje przez `DesktopUserProcessStarter` z tokenem pulpitu,
  nie z tokenem podniesionego launchera. Maksymalizuje istniejące okno
  zamiast dublować procesy.
- `src/Dismode.UI`: niepodwyższone, unpackaged WinUI 3 x64; automatyczna
  biblioteka gier, Tryb gry, trwałe reguły per gra, historia, diagnostyka
  oraz FPS/czas klatki ze stanem źródła PresentMon. Osobne natywne okno
  nakładki jest topmost, nie aktywuje się, przepuszcza wejście i znika poza
  aktywną grą; użytkownik może je wyłączyć oraz ustawić krycie, skalę i róg.
  Play w Bibliotece używa priorytetu `Normal` i prosi SessionHost wyłącznie
  o bezpieczne zastosowanie wcześniej zapisanych reguł procesów tej gry.
  Domyślnym widokiem jest przegląd Bento z natywnym Mica; overlay 344×152 ma
  oddzielne moduły FPS, frametime i źródła pomiaru.
- `src/Dismode.SessionHost`: backend wymagający UAC i host gRPC na pipe
  konkretnego SID; klasyfikuje procesy, prowadzi transakcyjną sesję oraz
  posiada proces PresentMon aktywnej gry.
- `src/Dismode.SystemAgent`: usługa Windows i host gRPC na zabezpieczonym
  pipe systemowym; udostępnia diagnostykę, sprzęt, katalog, A/B, profile,
  historię i recovery. Bez zaufanego podpisu klientów pozostaje read-only.
- `src/Dismode.SystemOptimizer`: niepodwyższony, osobny interfejs WinUI 3
  uruchamiany na żądanie, bez trayu; nie wykonuje mutacji bezpośrednio.
- `src/Dismode.Core`: niezależna od platformy domena; zawiera tożsamość procesu, maszynę stanów sesji, odwracalne działania, Hard Safety Policy, recovery i walidację żądań IPC.
- `src/Dismode.Windows`: adaptery Windows; read-only procesy/usługi, ACL oraz klient i serwer gRPC przez named pipes.
- `src/Dismode.Contracts`: Protobuf/gRPC, zamknięte komendy, wersjonowanie i metadata żądań.
- `src/Dismode.Data`: ścieżki danych i niezależny append-only recovery journal z łańcuchem SHA-256.
- `tests/Dismode.UnitTests`: testy czystej domeny i kontraktów.
- `tests/Dismode.RecoveryTests`: journal integrity, partial-write detection, crash injection, idempotencja, konflikty i odwrotna kolejność rollbacku.
- `tests/Dismode.IntegrationTests`: rzeczywista enumeracja procesów i usług w trybie read-only.
- `tests/Dismode.SecurityTests`: deskryptor ACL bez szerokich principalów.
- `tools/Dismode.SessionSimulator`: klient dwóch kanałów gRPC do smoke
  testów bez mutacji oraz `--presentmon-status` do kontroli przypiętego
  składnika FPS.
- `tools/Dismode.ProcessTestHarness`: kontrolowana aplikacja okienkowa do testów priorytetu, EcoQoS, graceful close, restartu i drzewa procesu.
- Projekty UI i system tests zostaną dodane dopiero z rzeczywistymi scenariuszami, aby nie pozostawiać pustych atrap.

## Entry points

- `src/Dismode.UI/App.xaml.cs`: start natywnego UI użytkownika jako `asInvoker`.
- `src/Dismode.Launcher/Program.cs`: widoczny entrypoint `Dismode.exe`; uruchamia host użytkownika, agenta read-only i UI z jednego katalogu wydania.
- `src/Dismode.SessionHost/Program.cs`: podwyższony proces sesji użytkownika,
  gRPC/HTTP2 wyłącznie przez `Dismode.User.{SID}`.
- `src/Dismode.SystemAgent/Program.cs`: host usługi Windows LocalSystem,
  gRPC/HTTP2 wyłącznie przez `Dismode.System`.
- Oba hosty obsługują `--diagnostics` jako jednorazowy, read-only raport konsolowy.
- Osobny updater powstanie dopiero po ustabilizowaniu MVP.

## Komendy

Obowiązujący kontrakt narzędziowy:

```powershell
dotnet restore Dismode.sln
dotnet build Dismode.sln --configuration Debug
dotnet test Dismode.sln --configuration Debug --no-build
dotnet format Dismode.sln --verify-no-changes
```

Lokalne wydanie klikalne powstaje w `artifacts\Dismode-App`. Projekty
Launcher, SessionHost i SystemAgent są publikowane przed UI; UI jest
publikowane jako ostatnie, a jego target MSBuild rekursywnie kopiuje
wszystkie skompilowane XBF z zachowaniem podkatalogów oraz aplikacyjny PRI.
Skrót
`C:\Users\Damia\Desktop\Dismode.lnk` wskazuje na to wydanie.

Samodzielny instalator powstaje przez:

```powershell
.\tools\Build-Installer.ps1
```

Skrypt tworzy self-contained payload win-x64, manifest wszystkich plików,
kompiluje `artifacts\installer\Dismode-Setup-<wersja>-win-x64.exe` i zapisuje
obok jego SHA-256. Instalator docelowo używa `Program Files\Dismode`,
skrótów Windows i standardowego deinstalatora.

Komendy są uruchamiane po każdej bramie. Ostatnia pełna regresja 0.1.7
zakończyła się kodem 0: build Release bez ostrzeżeń, format bez zmian i
128/128 testów.

## Inwarianty bezpieczeństwa

- UI nigdy nie wykonuje bezpośrednio operacji uprzywilejowanych.
- IPC udostępnia wyłącznie zamknięty katalog typowanych komend; brak arbitralnego PowerShella, procesu, ścieżki rejestru lub polecenia.
- Każda zmiana systemowa ma kolejność: walidacja, zapis intencji i stanu oryginalnego, apply, verify, zapis wyniku.
- Journal odzyskiwania jest append-only i niezależny od SQLite.
- Operacje apply/restore są idempotentne.
- Rollback wykonuje trójstronne uzgadnianie stanu `original/applied/current`; nie nadpisuje ślepo zmian zewnętrznych.
- PID bez czasu utworzenia, ścieżki i SID nie identyfikuje procesu.
- Hard Safety Policy ma wyższy priorytet niż reguły użytkownika.
- Brak rzeczywistych zmian produkcyjnych usług lub planu zasilania przed przetestowaniem snapshotu, journalu i recovery na kontrolowanych adapterach testowych.
- Nie wyłączać Defendera, zapory, Windows Update na stałe, krytycznych usług, urządzeń, anti-cheat ani zabezpieczeń systemowych.
- Nie ustawiać `Realtime`; rzeczywisty `High` jest maksymalnym poziomem
  dopuszczonym przez Hard Safety i musi zostać potwierdzony odczytem Windows.
- Nie obiecywać wzrostu FPS bez rzeczywistego pomiaru.

## Znane problemy i braki

- Journal i recovery sterują rzeczywistymi akcjami procesu w SessionHost oraz
  wydzielonym runtime SystemAgent. Katalog 0.4.0 udostępnia tylko adaptery z
  niezależnym readbackiem; pozostałe kategorie są jawnie `Unsupported`.
- User Database SQLite ma schemat v11, automatyczne i ręczne profile,
  historię z interwałowymi statystykami FPS/frametime, reguły
  priorytetu/akcji per gra oraz globalne preferencje HUD (włączenie,
  przezroczystość, skala i róg), a także lokalne metadane Steam/Epic z TTL;
  Osobna baza maszyny System Optimizer ma schemat v1.
- Systemowy pipe używa ACL dla SYSTEM i administratorów, odczytuje rzeczywisty
  SID oraz PID klienta i ponownie weryfikuje tożsamość procesu. Produkcyjne
  mutacje pozostają zablokowane do czasu dostarczenia certyfikatu wydawcy.
- Na hoście nie ma skonfigurowanego produkcyjnego certyfikatu Authenticode.
  `Build-Installer.ps1` celowo przerywa przed budową zamiast tworzyć
  niepodpisane wydanie 0.4.0.
- Klikalne wydanie `Dismode-App` pozostaje framework-dependent; instalator
  0.1.7 używa osobnego self-contained payloadu i nie wymaga zewnętrznego .NET
  ani Windows App Runtime.
- Instalator zawiera rejestrację, aktualizację i bezpieczną deinstalację usługi
  SystemAgent, lecz rzeczywisty smoke instalacji/restartu wymaga kontrolowanej
  VM i produkcyjnego podpisu.
- Zarządzany profil zasilania bezpiecznie kopiuje i usuwa własny schemat, ale nie zmienia jeszcze parametrów wydajnościowych; nie wolno przedstawiać go jako wzrostu FPS.
- Rozmowa audytowa rekomenduje część fundamentów, które repo już ma:
  .NET 10, wspólny kontrakt odwracalnej akcji, append-only journal, recovery,
  allowlistowane IPC i ACL named pipe. Nie należy tworzyć ich równoległych
  implementacji; kolejne etapy mają rozwijać istniejące systemy.
- Testy awaryjne wymagające restartu, VM lub instalacji usługi nie mogą być wykonywane bez osobnego, kontrolowanego środowiska.
- Pomiar FPS zależy od dostępności zdarzeń ETW dla konkretnej gry. Brak lub
  nieświeże dane pozostają jawnie niedostępne; Dismode nie uruchamia gry
  ponownie tylko po to, aby wymusić pomiar.

## Ostatnio zmieniane pliki

- Ikona zasobnika po restarcie Eksploratora i rura aktywacji bez zawieszeń
  (2026-09-18):
  - `TrayIconService`: rejestruje komunikat `TaskbarCreated`
    (`RegisterWindowMessage`, `ChangeWindowMessageFilterEx` = MSGFLT_ALLOW,
    gdyby UI szło jako administrator) i po nim dodaje ikonę od nowa
    (`NIM_ADD`, awaryjnie `NIM_MODIFY`, `NIM_SETVERSION`). Dotąd po
    restarcie Eksploratora ikona znikała, a schowane okno traciło jedyne
    wejście.
  - `UiActivationServer` przeniesiony z `Dismode.UI/Services` do
    `Dismode.Core/Activation` (testowalny): rura tworzona wewnątrz
    pętli (zajęta nazwa = ponowna próba co 1 s, nie cicha śmierć zadania),
    limit czasu na obsługę połączenia (`connectionDeadline`, domyślnie
    10 s), szerszy filtr wyjątków. Usunięte `Flush` po zapisie do rury po
    obu stronach (`UiActivationClient`, serwer): `PipeStream.Flush` to
    `FlushFileBuffers`, które czeka, aż druga strona odczyta dane; klient,
    który się połączył i zamilkł, wieszał serwer na zawsze w `TryReject`.
    Zapis do rury jest niebuforowany, więc `Flush` nic nie wnosił.
  - `Dismode.Launcher`: własny klient rury zastąpiony
    `UiActivationClient.TrySend`; SessionHost startuje z
    `requireElevation: true` (launcher jest podniesiony, więc `runas` nie
    pyta), zamiast najpierw próbować tokenu pulpitu, który host odrzuca
    błędem 740.
  - Testy `UiActivationServerTests` (unit): milczący klient nie blokuje
    następnego żądania, śmieci i za duża ramka = odmowa i dalsza obsługa,
    powtórzony `RequestId` odrzucony, `Dispose` przestaje nasłuchiwać.
- Token pulpitu dla procesów startowanych z podniesionych komponentów
  (2026-09-16):
  - Wada: `Process.Start` dziedziczy token wywołującego, więc gra
    uruchomiona przez podniesiony SessionHost, aplikacja przywracana po
    sesji (`GracefulCloseApplicationAction`, np. Discord) i UI startowane
    z launchera `requireAdministrator` działały jako administrator: zapisy
    gry z cudzymi uprawnieniami, przeciąganie z Eksploratora blokowane
    przez UIPI, nakładki i aktualizatory sklepów odmawiające pracy,
    pełne prawa bez potrzeby.
  - `DesktopUserProcessStarter` (Dismode.Windows/Processes): gdy
    wywołujący jest podniesiony, tworzy proces przez `CreateProcessW` z
    atrybutem `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` wskazującym powłokę
    pulpitu (`GetShellWindow`, awaryjnie najstarszy `explorer.exe` sesji);
    dziecko dziedziczy token, priorytet i powinowactwo powłoki. Brak
    powłoki albo błąd Win32 = zwykły start jak dotąd, powód w
    `StartedProcess.FallbackReason`. Proces niepodniesiony startuje
    zwyczajnie. `StartAsChildOf(startInfo, pid)` bez awaryjnego startu
    (testy). `StartedProcess`: `Id`, `HasExited` (uchwyt procesu),
    `InheritsParentToken`. `WindowsCommandLine.Build`: cytowanie jak
    `CommandLineToArgvW`/`ArgumentList`.
  - Użycie: `ManualGameProfileLauncher` (start gry),
    `GracefulCloseApplicationAction` (przywracanie), `Dismode.Launcher`
    (`TryStart` bez elewacji; trzy pliki dołączone jako `<Compile Link>`,
    bo launcher zależy tylko od Core). Instalator aktualizacji i PresentMon
    zostają podniesione celowo.
  - Testy (`tests/Dismode.IntegrationTests/Processes`): dziecko
    wskazanego rodzica ma jego PID rodzica i odziedziczone powinowactwo,
    `HasExited` po zabiciu, katalog roboczy i argumenty docierają do
    `cmd.exe`, brak programu = `Win32Exception` 2, ścieżka względna i
    `UseShellExecute` odrzucane; round-trip cytowania przez
    `CommandLineToArgvW`. Gałąź „podniesiony → powłoka" sprawdza się sama
    tylko przy uruchomieniu testów jako administrator.
- Automatyczna optymalizacja wykrytej gry (2026-09-12):
  - `GameDetectionPreferences` (Core) i tabela `GameDetectionPreferences`
    (schemat 12) w `SqliteUserDataStore`: jedno ustawienie „automatycznie
    optymalizuj wykrytą grę", domyślnie włączone, zapisywane w bazie.
  - `MainWindow.xaml.cs`: skan uruchomionej gry działa także w zasobniku
    (takt 6 s zamiast 15 s, gdy automat czuwa); po wykryciu automat robi to,
    co przycisk „Optymalizuj w locie" — plan z zapisanymi regułami i start
    sesji. Egzemplarze gry, której sesja się skończyła (Przywróć, Zamknij
    grę, wyjście z gry, odtwarzanie), oraz egzemplarz z nieudanym startem
    są pomijane po PID i czasie startu procesu, aż znikną; nowe
    uruchomienie gry znów się kwalifikuje. Skan bierze tylko procesy
    z bieżącej sesji Windows, czyta ścieżkę przez `ProcessImagePath`
    (`QueryFullProcessImageName`), więc widzi też gry pod DRM, a przy dwóch
    egzemplarzach tej samej gry automat nie próbuje. Stara odpowiedź
    odpytywania hosta nie nadpisuje stanu sesji zmienionego w międzyczasie;
    automat stoi, gdy czeka start zlecony z zewnątrz albo gdy użytkownik
    siedzi na stronie planu z wynikiem analizy aplikacji w tle. Rejestr
    pomijanych egzemplarzy to `AutomaticOptimizationSkipList` (Core,
    testy jednostkowe); tuż przed dołączeniem automat sprawdza jeszcze,
    czy profil jest w bibliotece i czy PID nadal należy do tego samego
    procesu (czas startu). Baner mówi wprost, gdy automat pomija
    egzemplarz.
  - Jeden egzemplarz i bramka startowa (16.09): `SingleInstanceLock` (Core,
    mutex `Local\Dismode.<komponent>.<hash SID>`, wspólny dla procesów
    z uprawnieniami i bez) w `Dismode.UI` (`StartupGate`) i w SessionHost
    (`Program.cs`, poza trybami pomocniczymi). Drugi UI oddaje żądanie
    pierwszemu przez rurę aktywacji (`UiActivationClient`, nowe pole
    `ShowOnly` w `UiActivationRequest`, stare launchery bez pola = start gry)
    i kończy pracę; drugi host kończy pracę kodem 3. UI bez hosta z tego
    samego katalogu uruchamia go z `runas`; odmowa UAC (1223) = komunikat
    i kod 4, host z innej lokalizacji = komunikat i kod 3. Powód: dwie kopie
    (artifacts + Program Files) walczyły o tę samą rurę, a UI bez hosta
    było atrapą.
  - Reguły dla wszystkich gier (16.09): `IGlobalBackgroundRuleRepository`
    + tabela `GlobalBackgroundProcessRules` (schemat 14), scalanie
    w `GameOptimizationPreferences.WithGlobalRules` (reguła gry, także
    „Ignoruj", wygrywa; limit 128), host scala je w `PrepareAsync` przy
    `use_saved_background_rules`, więc automat z zasobnika zamyka lub
    ogranicza te same aplikacje przy każdej wykrytej grze. UI: pole „także
    dla wszystkich innych gier" na stronie planu (gaśnie po zapisie),
    licznik i „Wyczyść" w ustawieniach. Testy: scalanie (Core), baza,
    sesja z regułą globalną na prawdziwym procesie i nadpisanie przez
    „Ignoruj".
  - Wzorce przeniesione z projektów otwartoźródłowych (16.09):
    `ForegroundWindowChangeListener` — hak `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`
    obok zapasowego taktu, z odbiciem 1 s, jak `ProcessManager` w
    HandheldCompanion (Playnite zostaje przy odpytywaniu
    `Process.GetProcesses()` z filtrem `SessionId`); wyjątek per gra
    `GameOptimizationPreferences.AutoOptimizeWhenDetected` (schemat 13,
    pole na stronie planu, zapis od razu) jak czarna lista Feral GameMode
    i wyłączenie wstrzykiwania per gra w Special K; podpowiedź ikony
    z nazwą gry aktywnej sesji.
  - Ścieżki kanoniczne (29.09, Playnite #913): `ExecutablePathIdentity`
    (`Dismode.Windows/Processes`) porównuje ścieżkę z biblioteki ze
    ścieżką, którą dla procesu podaje jądro — najpierw tekstem, a dopiero
    gdy ten się różni, przez `GetFinalPathNameByHandle` na uchwycie bez
    żadnego dostępu (działa na otwartym pliku gry). Biblioteka Steam
    przeniesiona junctionem zapisuje ścieżkę przed dowiązaniem, a
    `QueryFullProcessImageName` podaje ścieżkę za nim, więc samo porównanie
    tekstu mówiło „to nie ta gra". Podpięte w `ProcessClassificationService`
    (główny proces gry i składniki z katalogu instalacyjnego — bez tego
    trafiały między aplikacje tła), `BackgroundApplicationGuard`,
    `ManualGameProfileLauncher` (dołączanie i `EnsureMatchesProfile`;
    o treści pliku i tak rozstrzyga SHA-256) oraz w skanie automatu i
    liście pomijanych egzemplarzy w `MainWindow`. Koszt: jedno otwarcie
    pliku i tylko wtedy, gdy porównanie tekstu zawiodło.
  - `TrayIconService`: pozycja menu z zaznaczeniem „Automatycznie optymalizuj
    wykryte gry", podpowiedź ikony mówi, czy automat czuwa.
  - `StartSessionRequest.attach_only` → `LocalGameSessionOrchestrator` →
    `ManualGameProfileLauncher`: automat dołącza wyłącznie do działającej
    gry i nigdy jej nie uruchamia; brak egzemplarza kończy start jak każdy
    nieudany launch (testy `AttachOnly*` w `GameSessionOrchestratorTests`).
    Przy okazji: błąd w preflight `StartAsync` (profil usunięty lub
    zmieniony, EXE nieczytelny) porzuca plan hosta zamiast trzymać go do
    wygaśnięcia i blokować wyłączenie.
  - Bez podbicia `ProtocolInfo.CurrentVersion` (6): test
    `SystemOptimizerContractTests` przypina ją, bo osobno instalowana usługa
    SystemAgent musi zostać zgodna. Nowe UI ze starym SessionHostem (tylko
    scenariusz deweloperski) zignoruje `attach_only` i zachowa się jak przed
    zmianą.
- Rozszerzenia wydania 0.1.9:
  - `SystemMemoryCleaner`: globalne czyszczenie pamięci podręcznej i buforów
    plików (`SetSystemFileCacheSize`) oraz zwalnianie working setu procesów tła.
  - `WindowsGamingEnvironmentDiagnostic`: diagnostyka HAGS (Hardware-Accelerated
    GPU Scheduling), stanu Windows Game Mode, nagrywania Game DVR oraz
    generowanie rekomendacji dla gracza.
  - `AutoGameDetectionService`: serwis automatycznego wykrywania uruchomionych
    gier w locie dla trybu Auto-Boost.
  - `EstimatedFrametimeStabilityPercent`: wskaźnik stabilności klatek i analiza
    mikroprzycięć w `SessionFrameRateStatistics`.
  - Pełna regresja 150/150 testów zdana w 100%.
- Optymalizacja pamięci RAM i doradca optymalizacji: `ProcessMemoryTrimmer`
  wykorzystuje Win32 `K32EmptyWorkingSet` i `SetProcessWorkingSetSize` do
  natychmiastowego uwalniania nieaktywnej pamięci RAM procesów tła do standby
  podczas nakładania trybu EcoQoS. `BackgroundOptimizationAdvisor` inteligentnie
  rekomenduje tryby `CloseAndRestore` vs `LowerPriorityAndEcoQos` w oparciu o
  aktywny preset oraz zużycie pamięci/procesora, a UI Bento udostępnia przycisk
  inteligentnej selekcji i dynamiczny wskaźnik odzysku pamięci RAM.
- Wykrywanie form factoru i ochrona zasilania PC: `DeviceFormFactor` (`Desktop`,
  `Laptop`, `Unknown`) i `WindowsDeviceFormFactorDetector` wykrywają typ maszyny
  przez Win32 `GetSystemPowerStatus` i flagi baterii. `HardSafetyPolicy` oraz
  `ActivateManagedPowerProfileAction` blokują modyfikację planu zasilania na
  komputerach stacjonarnych (`DesktopPowerPlanPreserved`), aby zachować aktywny
  profil użytkownika (np. Wysoka wydajność / własne ustawienia).
- Bezpieczne zamykanie okien na Windows 11: `ProcessWindowHelper` i
  `WindowNativeMethods` filtrują systemowe nakładki Windows 11 (np.
  `UAC_InputIndicatorOverlayWnd`, IME, tooltipy) i wysyłają `WM_CLOSE` do
  właściwych okien aplikacji, usuwając problem z fałszywym `MainWindowHandle`
  w .NET.
- Finalny instalator:
  `artifacts\installer\Dismode-Setup-0.1.9-win-x64.exe`, SHA-256
  `EA8711A3C5543470B65F14ECCD1703559BFA02A8034641DF7D7CC73455D7B0DB`.
  Zainstalowane własne binaria mają 0.1.9.0, PDB = 0, sparse package ma
  0.1.9.0, UI hash odpowiada payloadowi.
- Lokalne metadane Epic: `EpicLocalGameMetadataProvider.cs` odczytuje bez
  sieci wyłącznie `LastPlayedGame` z pliku ustawień launchera (limit 4 MiB,
  `FileShare.ReadWrite/Delete`), dopasowuje `CatalogItemId` i zachowuje
  najnowszy czas. `GameMetadataRefreshService` rejestruje provider domyślnie
  oraz jednokrotnie wzbogaca świeże placeholdery, po czym respektuje TTL.
  Kontrolowane testy provider/TTL zakończyły się wynikiem 5/5.
- Infrastruktura redesignu UI: `Dismode.UI.csproj` nie zawiera już
  sztywnej listy trzech XBF. Target publish odkrywa wszystkie skompilowane
  XBF rekursywnie, zachowuje ich ścieżki względne i kopiuje PRI wyprowadzone
  z `$(TargetName)`. `UiResourceReferenceTests` analizuje wszystkie źródłowe
  XAML-e w podkatalogach z pominięciem `bin/obj` i chroni ten kontrakt
  publikacji.
- Integracja powłoki 0.1.2: nowy natywny projekt
  `src/Dismode.ShellExtension` implementuje zamknięty `IExplorerCommand`
  „Uruchom przez Dismode” dla pojedynczego istniejącego pliku `.exe`.
  `installer/sparse-package/*` rejestruje podpisany sparse MSIX z zewnętrzną
  lokalizacją, a legacy verb pozostaje fallbackiem dla „Pokaż więcej opcji”.
- `NativeTrayIcon.cs` zastępuje zależność Windows Forms natywnym
  `Shell_NotifyIconW`; uruchomienie z menu kontekstowego używa
  `--launch-through-dismode <exe> --background`, zachowuje pojedynczą
  instancję UI i pozostawia Dismode w zasobniku zamiast na ekranie.
- `Build-LocalRelease.ps1`, `Build-Installer.ps1` i `Dismode.iss` budują
  samowystarczalny payload 0.1.2, podpisują deweloperski sparse MSIX,
  usuwają PDB/pozostałości Windows Forms i wykonują testy projektów
  sekwencyjnie (`-m:1`), aby testowe okna nie zatruwały równoległych prób.
- Szybki Play: `LocalGameSessionOrchestrator.cs`,
  `ManualGameProfileLauncher.cs` i `SessionStateMachine.cs` uruchamiają grę
  po checkpointcie, przed sekwencyjnymi akcjami tła, oraz usuwają jedno
  redundantne hashowanie pre-launch bez osłabienia weryfikacji uruchomionego
  procesu.
- Bento UI: `App.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`,
  `PerformanceOverlayWindow.xaml` i `.xaml.cs` dodają Mica, nową hierarchię
  kart, domyślny przegląd Bento oraz uproszczony HUD 344×152.
- `SessionStateMachineTests.cs` i `SavedRuleGameSessionTests.cs` chronią nową
  kolejność stanów i checkpointów launch-first.
- Safety Release 0.1.1: `GameOptimizationPreferences.cs`,
  `SqliteUserDataStore.cs`, `SessionClientService.cs`,
  `DismodeSessionGrpcService.cs` i `LocalGameSessionOrchestrator.cs`
  ustawiają konserwatywne `Normal` oraz odłączają zapisany boost od szybkiego
  Play.
- Safety Release 0.1.1: `MainWindow.xaml`, `MainWindow.xaml.cs`,
  `BackgroundApplicationListItem.cs`, `ProcessClassificationService.cs` i
  `ServiceClassificationService.cs` wymagają ręcznego wyboru nowych działań,
  uczciwie opisują EcoQoS/working set oraz pozostawiają usługi read-only.
- `SavedRuleGameSessionTests.cs` i `UserDataStoreTests.cs` chronią brak
  automatycznego boostu oraz domyślny priorytet `Normal`.

- `Dismode.sln`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`: deterministyczny fundament .NET 10.
- `src/Dismode.UI/*`: minimalne, natywne WinUI 3 w trybie unpackaged x64.
- `src/Dismode.SessionHost/*`, `src/Dismode.SystemAgent/*`: diagnostyczne entrypointy bez mutacji.
- `src/Dismode.Core/*`, `src/Dismode.Contracts/*`, `src/Dismode.Data/*`, `src/Dismode.Windows/*`: początkowe granice modułów.
- `src/Dismode.Core/Domain`, `Actions`, `Sessions`, `Policies`, `Recovery`: implementacja Bramy B.
- `src/Dismode.Core/Journal`, `Transactions`, `Recovery`: koordynacja zapisu przed zmianą, checkpointy i recovery Bramy C.
- `src/Dismode.Data/Journal/AppendOnlyRecoveryJournal.cs`: trwały JSONL journal, write-through, flush-to-disk i integralność SHA-256.
- `src/Dismode.Contracts/Commands` i `Protocol`: zamknięte komendy, wersjonowanie oraz idempotency key.
- `src/Dismode.Contracts/Grpc/dismode.proto`: protokół v4; discovery z
  klasyfikacją, priorytetem i całkowitymi licznikami niezależnymi od limitu
  zwracanej listy, tryb `BelowNormal + EcoQoS`, telemetria FPS oraz
  zamknięte RPC prepare/start/restore/close-game. Jawna flaga prepare dla
  szybkiego Play nakazuje backendowi wczytać zapisane reguły zamiast ufać
  liście UI; `CloseGameRequest` ma zamkniętą flagę natychmiastowego
  zakończenia bez dowolnego PID.
- `src/Dismode.Windows/Processes`, `Services`, `Ipc`, `Security`: read-only discovery oraz transport z chronionym ACL.
- `src/Dismode.Windows/Processes`: pełna tożsamość z hashem i SID, priorytet,
  EcoQoS, graceful close, bezpieczny restart, trwałe śledzenie drzewa gry,
  lekka telemetria oraz kontrolowany provider PresentMon.
- `src/Dismode.Windows/Profiles`: wykrywanie Steam, Epic, GOG, Roblox i
  dostępnych instalacji Xbox na podstawie `MicrosoftGame.Config`,
  synchronizacja wersjonowanych EXE oraz uruchamianie albo dołączanie do
  jednego już działającego procesu po ponownej walidacji ścieżki, SHA-256,
  SID i sesji.
- `src/Dismode.Core/Profiles`, `History` oraz
  `src/Dismode.Data/UserData`: profile, trwałe reguły per gra, historia i
  parametryzowany SQLite z WAL. Migracje v3–v5 zachowują wcześniejsze reguły
  i historię; v5 dodaje wyłącznie rzeczywiście zmierzone statystyki sesji.
- `src/Dismode.Core/Profiles/IGameOptimizationPreferencesRepository.cs`,
  `src/Dismode.Windows/Sessions/SavedBackgroundRuleResolver.cs` oraz
  `LocalGameSessionOrchestrator.cs`: szybki Play rozwiązuje zapisane reguły
  względem bieżących procesów po pełnej ścieżce EXE, ponownie klasyfikuje i
  zatwierdza cele w SessionHost, a nieaktualne lub niedozwolone dopasowania
  pomija fail-closed i pokazuje ich podsumowanie w planie.
- `src/Dismode.Core/Services`, `Policies/ProtectedServiceCatalog.cs` oraz `src/Dismode.Windows/Services`: krótkotrwała zgoda, snapshot SCM, blokady chronionych/driver/shared/trigger/dependent i odwracalne zatrzymanie bez zmiany start type.
- `src/Dismode.Core/Recovery/IRecoveryDecisionProvider.cs` oraz `src/Dismode.Windows/Power`: częściowe recovery wieloetapowego tworzenia, aktywacji i usuwania schematu należącego do Dismode.
- `src/Dismode.SessionHost/Program.cs`, `src/Dismode.SystemAgent/Program.cs`: dwa hosty bez TCP i bez mutacji.
- `src/Dismode.Launcher/*`: jeden klikalny launcher, stała allow-lista trzech komponentów, wykrywanie dokładnej ścieżki już działających procesów i komunikat błędu bez konsoli.
- `src/Dismode.UI/Services/SessionClientService.cs`,
  `MainWindow.xaml(.cs)`: automatyczna biblioteka, wybór
  zamknij/BelowNormal/BelowNormal+EcoQoS/ignoruj, trwałe reguły, plan, jawna
  zgoda, start, ręczne przywrócenie, łagodne zamknięcie gry oraz panel
  FPS/ms z automatycznym uruchamianiem przypiętego PresentMon.
- `assets/branding/Dismode.png`, `Dismode.ico` oraz kopie w projektach UI
  i Launcher: własna ikona aplikacji i skrótu.
- `src/Dismode.UI/Dismode.UI.csproj`: jawne kopiowanie luźnych zasobów WinUI do katalogu publikacji.
- `Directory.Build.props`, `tools/Build-LocalRelease.ps1`,
  `tools/Build-Installer.ps1`, `installer/*`: wersja produktu 0.1.0,
  opcjonalna publikacja self-contained, bezpieczny instalator Inno Setup,
  manifest payloadu i suma SHA-256 gotowego EXE.
- `src/Dismode.UI/MainWindow.xaml.cs`, `ViewModels/*`: plan jest unieważniany po utracie SessionHost, a elementy list mają czytelne nazwy dla automatyzacji i Narratora.
- `src/Dismode.Windows/Processes/IProcessIdentityProvider.cs`, `ProcessIdentityProvider.cs` oraz `Sessions/LocalGameSessionOrchestrator.cs`: aktywny monitoring sprawdza PID, czas startu, ścieżkę, SID i sesję bez ponownego hashowania EXE; pełny SHA-256 nadal obowiązuje przy przygotowaniu, uruchomieniu, recovery i przed mutacją.
- `src/Dismode.Windows/Processes/ProcessClassificationService.cs` oraz
  `src/Dismode.UI/MainWindow.xaml.cs`: kandydaci tła są ponownie
  klasyfikowani względem wybranej gry; główny proces i pomocniki z jej
  katalogu instalacyjnego są chronione przed zamknięciem i obniżeniem
  priorytetu.
- `src/Dismode.Windows/Processes/RuntimeProcessEcoQosAction.cs` oraz
  `Sessions/LocalGameSessionOrchestrator.cs`: produkcyjny EcoQoS ma osobną
  transakcję i journal, jest przywracany przed priorytetem i bezpiecznie
  kończy recovery, jeżeli proces sam zniknął.
- `src/Dismode.Windows/Processes/GameProcessTreeSessionTracker.cs` oraz
  `Sessions/LocalGameSessionOrchestrator.cs`: potomkowie launchera są
  identyfikowani raz, potem lekko monitorowani i zapisywani do journalu;
  recovery utrzymuje sesję, gdy proces główny zniknął, ale gra nadal działa.
- `src/Dismode.Windows/Processes/PresentMon*`: kontrola wersji, rozmiaru i
  SHA-256, parser CSV v2, filtr śledzonych PID i cykl życia procesu pomiarowego
  związany z aktywną sesją. Provider wybiera dokładnie jeden zweryfikowany
  proces renderujący, preferując proces z oknem i największym working set;
  nie przekazuje już wielu nieobsługiwanych filtrów `--process_id`. Nazwa
  sesji ETW jest stabilna i ograniczona skrótem SID użytkownika, start usuwa
  poprzednią sesję o tej samej nazwie, a stop najpierw jawnie ją kończy.
  Pomiar używa lekkiego zestawu zdarzeń potrzebnych do FPS i wykonuje jedno
  kontrolowane ponowienie po braku pierwszych klatek.
- `src/Dismode.UI/PerformanceOverlayWindow.xaml(.cs)`: natywna nakładka
  FPS/frametime bez przechwytywania fokusu i wejścia. HUD v2 pokazuje duży
  FPS, frametime, stan LIVE/SYNC, rzeczywisty trend próbek oraz PID źródła,
  wybiera monitor okna gry i poprawnie skaluje się per-monitor DPI.
  Regulacja krycia używa kanału alpha całego natywnego okna
  `WS_EX_LAYERED`, zamiast wygaszać zawartość XAML do ciemnego tła; wynik
  `SetLayeredWindowAttributes` jest każdorazowo odczytywany i weryfikowany.
- `src/Dismode.UI/MainWindow.xaml(.cs)`: profil Agresywny analizuje do 64
  bezpiecznych kandydatów, wybiera zamknięcie restartowalnych aplikacji z
  oknem, a pozostałym nadaje `BelowNormal + EcoQoS`; czerwony przycisk kończy
  grę bez dodatkowego dialogu.
- `src/Dismode.Windows/Services/ServiceInventory.cs`,
  `ServiceClassificationService.cs` oraz `src/Dismode.UI/MainWindow.xaml*`:
  inwentaryzacja nie pomija już usługi po niepełnym odczycie konfiguracji;
  taki rekord jest widoczny i konserwatywnie wyłączony z optymalizacji, a UI
  pokazuje pełną listę usług zamiast pierwszych 100.
- `src/Dismode.UI/Services/DiagnosticsProbeService.cs`: kafle diagnostyczne
  pokazują całkowite liczby procesów i usług; usunięto sztuczny limit 100.
- `src/Dismode.UI/App.xaml`, `MainWindow.xaml`: wspólny gamingowy system
  wizualny z granatowym gradientem, neonowym akcentem, nową typografią,
  kartami, hero pulpitu, adaptacyjną nawigacją i paskiem poleceń biblioteki,
  który nie ściska tytułu przy węższym oknie.
- `src/Dismode.UI/MainWindow.xaml.cs`: natywny pasek tytułu Windows ma
  kolory zsynchronizowane z jasnym/ciemnym motywem; w trybie ciemnym białe
  tło zastępuje `#080B12`, a przyciski systemowe mają własne stany hover,
  pressed i inactive.
- `src/Dismode.UI/MainWindow.xaml(.cs)`,
  `ViewModels/ProfileListItem.cs`: kafelki Play w Bibliotece są natywnymi,
  dostępnymi przyciskami. Jedno kliknięcie wybiera profil i uruchamia
  bezpieczny przepływ `prepare/start` przez SessionHost z zapisanym
  priorytetem gry; UI nie wykonuje bezpośredniego `Process.Start`.
- `src/Dismode.UI/PerformanceOverlayWindow.*`,
  `src/Dismode.Core/Profiles/PerformanceOverlayPreferences.cs`,
  `src/Dismode.Data/UserData/SqliteUserDataStore.cs`: HUD jest pokazywany
  wyłącznie dla foreground PID śledzonej gry, a przełącznik,
  przezroczystość 35–100%, skala 75–150% i cztery rogi są zapisane w SQLite
  (preferencje wprowadzone w migracji v4).
- `src/Dismode.Core/History/SessionFrameRateStatistics.cs`,
  `src/Dismode.Windows/Sessions/SessionFrameRateStatisticsAccumulator.cs`,
  `src/Dismode.Data/UserData/SqliteUserDataStore.cs` oraz
  `src/Dismode.UI/ViewModels/HistoryListItem.cs`: prawidłowe próbki
  `Measuring` są agregowane bez drugiego miernika, zapisane przez migrację v5
  i opisane jako średnia/najniższa próbka, a nie benchmark lub 1% low.
- `tools/Dismode.SessionSimulator/*`: rzeczywisty klient obu named pipes.
- `tests/Dismode.UnitTests/*`: 27 testów fundamentu, domeny, serializacji
  tożsamości, statystyk sesji i walidacji IPC.
- `tests/Dismode.RecoveryTests/*`: 12 testów journalu, awarii i rollbacku.
- `tests/Dismode.IntegrationTests/*`: 52 testy enumeracji Windows,
  rzeczywistych akcji na własnym process harnessie, profili, SQLite,
  migracji v4→v5, statystyk FPS, odzyskiwania sesji i potomka launchera,
  lekkiego monitoringu, PresentMon, zapisanych reguł szybkiego Play oraz
  symulowanych akcji usług i zasilania.
- `tests/Dismode.SecurityTests/*`: 3 testy allow-listy ACL.
- `docs/CODEX_CONTEXT.md` i `docs/exec-plans/active/dismode-mvp.md`: bieżący stan Bram G–H.

## Ostatnia walidacja wydania

- Lokalna regresja System Optimizer 0.4.0: `Dismode.sln` przywrócono i
  zbudowano w Debug bez ostrzeżeń; przeszło 285/285 testów (112 Unit,
  156 Integration, 13 Recovery, 4 Security), a format jest czysty. Osobne
  rozwiązanie Memory Optimizer zbudowano w Release bez ostrzeżeń; przeszło
  70 testów, jeden agresywny test VM został prawidłowo pominięty i format jest
  czysty. Worker aktualizacji przeszedł 5/5 testów, typecheck, generowanie
  typów i suchy build bez deploymentu. Inno Setup 6.7.3 skompilował skrypt
  instalatora na minimalnym tymczasowym payloadzie. Wszystkie 13 skryptów
  parsują się pod Windows PowerShell 5.1, a brak produkcyjnego certyfikatu
  celowo blokuje budowę wydania przed utworzeniem instalatora. Nie uruchamiano
  UI, instalatora, gry, usług ani mutacji systemowych.
- Najnowsza poprawka stabilnego układu Memory Optimizer zarezerwowała miejsce
  na status i postęp oraz ograniczyła zmiany geometrii okna do świadomego
  przełączenia trybu. Odświeżenie statusu, zapis i optymalizacja nie wywołują
  już `MoveAndResize`; pasek statusu ma stałą wysokość i jednoliniowe komunikaty.
  Pełny komponent ma 58/58 testów Core i 12/12 Security (łącznie 70 zaliczonych
  oraz 1 kontrolowany test agresywny pominięty bez flagi VM). Payload i instalator 0.3.0
  przebudowano bez uruchamiania UI ani sterowania pulpitem.
  Instalator `artifacts\\installer\\Dismode-Setup-0.3.0-win-x64.exe` ma
  163 143 906 B i SHA-256
  `0928FACF8DE43BBAC3B6AC3C96F4675865E54005D2C7B5EFF07A09FBD051DDCA`;
  odpowiadające źródło GPL ma SHA-256
  `22B799207B2CCCC4BA6F85953191AD033D660D7735DEBD86FB7484159EA36EED`.
- Ostatnie `Build-Installer.ps1 -SkipTests` dla Dismode 0.3.0 zakończyło
  lokalny build i staging bez instalacji, UAC, uruchamiania aplikacji/gier ani
  deploymentu. Release ma 0 ostrzeżeń i 0 błędów. Świeży
  self-contained `artifacts\Dismode-App` ma manifest 703/703 plików,
  publiczne artefakty mają 0 PDB, a własne binaria mają wersję `0.3.0.0`.
  Instalator pozostaje niepodpisany Authenticode; instalacji nie wykonywano.
- Lokalny staging `artifacts\update-service-0.3.0` zawiera podpisany manifest
  preview `0.3.0` z `minimumSupportedVersion=0.1.1` i osiem chunków.
  Brama publikatora ECDSA oraz zgodność rozmiaru i SHA-256 chunków z finalnym
  instalatorem przeszły. Smoke instalacji, kontrola UI/DPI, rejestracja sparse
  package i końcowe usunięcie `bin/obj` nie zostały wykonane i pozostają po
  stronie kontrolera.
- Hotfix startu Bento UI usunął trzy odwołania do nieistniejącego zasobu
  `DismodeCardBackgroundBrush`; właściwy klucz to `DismodeCardBrush`.
  `App.xaml.cs` zapisuje lokalnie pełny wyjątek startowy, a nowy test
  `UiResourceReferenceTests` blokuje brakujące własne klucze zasobów XAML.
  Kontrolowany build diagnostyczny po poprawce pozostał responsywny i nie
  dopisał nowego błędu.
- Finalny build `Dismode-Setup-0.1.2-win-x64.exe` zakończył się kodem 0:
  Release 0 ostrzeżeń, journal czysty (797 rekordów), format bez zmian oraz
  106/106 testów (36 Unit, 13 Recovery, 54 Integration, 3 Security).
  Instalator ma 71 728 335 B i SHA-256
  `F4CC1136A92BC68622CFEC00247F9DA089F87C5266407AFA7D9C35EB82952742`.
  Payload ma 0 PDB i 0 plików `System.Windows.Forms*`; launcher i natywna DLL
  powłoki mają wersję `0.1.2.0`. DLL eksportuje `DllGetClassObject` oraz
  `DllCanUnloadNow`. Certyfikat `CN=Dismode Development` dołączony do
  payloadu ma ten sam thumbprint co podpis sparse MSIX. Instalator EXE nadal
  jest niepodpisany Authenticode.
- Czysta instalacja i późniejszy hotfix 0.1.2 zakończyły się kodem 0 po
  widocznej zgodzie UAC. Zainstalowane DLL/XBF i rozszerzenie powłoki mają
  hashe zgodne z payloadem; pakiet `Dismode.Desktop_0.1.2.0_x64` ma stan
  `Ok`, a klasa COM menu została rzeczywiście aktywowana. UI PID `20408`
  odpowiada, ma maksymalizację `ShowCommand=3` i brak nowego crash logu.
  SessionHost jest `Ready`, SystemAgent `ReadOnly`, pełne liczniki zwracają
  279 procesów i 317 usług, brak aktywnej sesji, journal pozostaje czysty.
- Po tym smoke użytkownik uruchomił Roblox przyciskiem Play. Windows potwierdził
  `RobloxPlayerBeta.exe` PID `5180` jako dziecko SessionHost, a kolejny
  read-only odczyt zwrócił sesję `Active`, `79,6 FPS`, `12,56 ms` i źródło
  PresentMon 2.5.1 przypięte do zweryfikowanego PID. Bieżący journal jest więc
  celowo aktywny do zakończenia gry; Codex nie zamknął ani nie restartował gry.
- Launch-first i Bento UI przeszły walidację poprawności XML trzech plików
  XAML, build Debug z 0 ostrzeżeń, format bez zmian oraz 95/95 testów.
  Test integracyjny potwierdza kolejność `GameLaunched` przed
  `ProcessesApplied` i późniejsze przywrócenie procesu tła. Nie uruchamiano
  aplikacji ani gry; rzeczywisty wygląd i czas kliknięcie→okno pozostają do
  ręcznej oceny użytkownika. Próba utworzenia osobnego podglądu została
  zatrzymana przed stagingiem przez bramę recovery z powodu sesji
  `dd95cdcd-a3ba-4518-ac97-3799cea08c15`; katalog podglądu nie powstał.
- Safety Release 0.1.1 przeszedł `dotnet build` Debug bez ostrzeżeń,
  `dotnet format --verify-no-changes` oraz pełne 95/95 testów (27 Unit,
  12 Recovery, 53 Integration, 3 Security). Regresja potwierdza, że zapisany
  `High` nie tworzy akcji `BOOST_GAME_PRIORITY` w szybkim Play, brak
  preferencji SQLite daje `Normal`, a jawna zapisana reguła procesu tła nadal
  działa i jest przywracana. Analiza punktów tworzenia potwierdziła brak
  produkcyjnego wywołania akcji zatrzymania usług i aktywacji sklonowanego
  planu zasilania. Nie uruchamiano UI, instalatora, usług ani gier.
- Instalator 0.1.0 przeszedł pełny build Release bez ostrzeżeń, format
  verification i 94/94 testy (27 Unit, 12 Recovery, 52 Integration,
  3 Security). Self-contained payload ma 706 plików i 256,56 MiB; manifest
  zweryfikował SHA-256 wszystkich 705 plików poprzedzających sam manifest.
  Każdy z czterech EXE ma wersję pliku `0.1.0.0`, a niepodwyższony
  `Dismode.SystemAgent.exe --diagnostics` uruchomił się z payloadu i
  zakończył kodem 0. Inno Setup 6.7.3 utworzył
  `artifacts/installer/Dismode-Setup-0.1.0-win-x64.exe` o rozmiarze
  71 823 084 B i SHA-256
  `164C642355EE5F59F0CB9BB652D3EDDED1A73434AF371800816A1F556CA4F630`.
  Suma zapisana obok EXE jest zgodna, nagłówek PE i metadane wersji są
  prawidłowe. Instalatora nie uruchomiono: bieżące komponenty Dismode nadal
  działają, a użytkownik zabronił sterowania komputerem; install/uninstall
  smoke pozostaje ręczną kontrolą po ich zamknięciu.
- Poprawka krycia HUD przeszła build Debug/Release projektu UI bez
  ostrzeżeń, format verification, 27/27 testów Unit i test round-trip
  preferencji SQLite. `Dismode.UI.dll` został podmieniony bez restartu
  backendu; kopia poprzedniego modułu znajduje się w
  `Dismode-App.ui-opacity-backup-20260729-2303`. Końcowy hash wdrożonego
  modułu to
  `733B238E0D047131B250243A9E305AD9D54F6644D4C4F2DF83B722C1E9E85581`.
  UI PID `4764` jest zmaksymalizowane, SessionHost PID `23872` i SystemAgent
  PID `23912` zachowały stan, a IPC potwierdziło brak aktywnej sesji.
  Nie uruchamiano gry wyłącznie w celu wizualnego testu; przy utworzeniu HUD
  kod odczytuje z Windows i weryfikuje zastosowany alpha/flags.
- Szybki Play z zapisanymi regułami przeszedł build Debug i Release bez
  ostrzeżeń, format verification oraz pełne 94/94 testy (27 Unit,
  12 Recovery, 52 Integration, 3 Security). Cztery nowe scenariusze
  potwierdziły exact-path, `Ignoruj`, ochronę procesu/sesji i duplikatów
  okien; pełny przepływ na kontrolowanym EXE rzeczywiście ustawił
  `BelowNormal`, po czym przywrócił `Normal`. Przed publikacją IPC
  potwierdziło brak aktywnej sesji, a journal był czysty (654 rekordy).
  Wydanie opublikowano do `artifacts/Dismode-App`, poprzednie zachowano
  jako `Dismode-App.backup-20260729-224628`. Smoke po UAC: SessionHost
  PID `23872` ma stan `Ready`, SystemAgent PID `23912` stan `ReadOnly`,
  UI PID `3088` ma tytuł `Dismode` i jest zmaksymalizowane (`showCmd=3`).
  Skrót pulpitu wskazuje na bieżący launcher, hash PresentMon jest zgodny,
  brak aktywnej gry, sesji i procesu pomiarowego.
- Wynik wydajności sesji przeszedł build Debug i Release bez ostrzeżeń,
  format verification oraz pełne 90/90 testów (27 Unit, 12 Recovery,
  48 Integration, 3 Security). Test pełnego przepływu potwierdził próbkę
  adaptera → zakończenie sesji → SQLite → Historia; osobny test potwierdził
  migrację v4→v5 z zachowaniem starego wpisu jako jawnego braku danych.
  Po naturalnym zakończeniu Robloxa read-only IPC potwierdził brak aktywnej
  sesji, a journal był czysty (654 rekordy). Spójne wydanie opublikowano do
  `artifacts/Dismode-App`, poprzednie zachowano jako
  `Dismode-App.backup-20260729-221808`, a skrót pulpitu wskazuje na nowy
  launcher. Smoke po UAC: SessionHost PID `12100` ma stan `Ready`,
  SystemAgent PID `21848` stan `ReadOnly`, UI PID `23104` odpowiada i jest
  zmaksymalizowane (`show=3`), SQLite ma schemat v5 oraz pięć wymaganych
  kolumn statystyk. Przy braku aktywnej gry nie ma widocznego okna HUD.
  Sesje zakończone przez wcześniejszy backend pozostają bez wymyślonych
  statystyk; agregacja działa dla sesji rozpoczętych przez nowe wydanie.
- Ustawienia HUD i widoczność zależna od foreground PID przeszły build Debug
  i Release bez ostrzeżeń, format verification oraz 83/83 testy
  (24 Unit, 12 Recovery, 44 Integration, 3 Security). Przed pełną regresją
  usunięto wyłącznie osierocony `Dismode.ProcessTestHarness.exe`; nie
  dotknięto gry. Nowe spójne wydanie działa z
  `artifacts/Dismode-App-overlay-settings-20260729-212445`: UI PID `21144`
  jest responsywne, SQLite ma schemat v4, SessionHost/SystemAgent/PresentMon
  zachowały PID `5936`/`1736`/`20796`, a Roblox zachował PID `19436`.
  Read-only IPC raportował `159,5 FPS` i `6,27 ms`; Win32 potwierdził
  foreground Robloxa oraz widoczny HUD 328×144 w prawym górnym rogu.
  Podczas tej walidacji współdzielone DLL były zablokowane przez aktywny
  backend, więc nie podmieniano ich podczas gry. Po naturalnym zakończeniu
  sesji ustawienia zostały promowane w spójnym wydaniu opisanym wyżej.
  Finalna etykieta suwaka to jednoznaczne „Krycie” z opisem kierunku; kopia
  UI sprzed tej korekty:
  `Dismode-App-overlay-settings-label-backup-20260729-2134`.
- Naprawa FPS/HUD v2 została wdrożona podczas aktywnej sesji bez
  uruchamiania ani zamykania gry przez Codex. Usunięcie siedmiu osieroconych
  sesji ETW natychmiast przywróciło pomiar Robloxa. Po restarcie wyłącznie
  składników Dismode i ręcznej zgodzie UAC recovery zachowało PID `19436`;
  SessionHost `Ready` raportował `240,1 FPS` i `4,16 ms`, SystemAgent
  `ReadOnly` zwrócił 316 usług, a na hoście pozostała dokładnie jedna
  stabilna sesja ETW `Dismode-9CD59278A121E901`. HUD jest widoczny,
  topmost, click-through i nieaktywujący; pełny render przy DPI 144 ma
  492×216 px. Build Debug/Release: 0 ostrzeżeń; wąskie testy PresentMon:
  5/5. Kopie sprzed podmian:
  `Dismode-App.ui-overlay-v2-backup-20260729-204516`,
  `Dismode-App.ui-dpi-backup-20260729-204820` i
  `Dismode-App.backend-etw-backup-20260729-205007`.
- Wydanie z natychmiastowym zakończeniem gry, nakładką i profilem Agresywnym
  opublikowano do `artifacts/Dismode-App`; poprzednie zachowano jako
  `Dismode-App.backup-20260729-202043`. Build Debug i Release zakończyły
  się bez ostrzeżeń, journal był czysty (353 rekordy), a trzy wąskie
  scenariusze przeszły 3/3: force-close na własnym harnessie, wybór procesu
  renderującego oraz własność sesji IPC. Po ręcznej zgodzie UAC końcowy smoke
  potwierdził jedno responsywne UI z oknem, jeden SessionHost `Ready`
  (protokół 4, 271 procesów), jeden SystemAgent `ReadOnly` (protokół 4,
  316 usług), brak aktywnej sesji oraz brak własnego procesu
  `PresentMon-2.5.1-x64`. Luźny zasób `PerformanceOverlayWindow.xbf` istnieje,
  a SHA-256 wdrożonego `Dismode.UI.dll` odpowiada wynikowi Release. Kopia
  sprzed hotfixu timeoutów UI:
  `Dismode-App.hotfix-backup-20260729-202410`. Roblox działał podczas
  publikacji, lecz przed końcowym odczytem został zamknięty poza poleceniami
  Codex; nie uruchamiano automatycznie Trybu gry.
- Migracja PresentMon: build Debug i Release zakończyły się bez ostrzeżeń,
  parser/integralność przeszły 3/3, a oficjalny stdout 2.5.1 dostarczył
  nagłówek i rzeczywistą klatkę przed zakończeniem procesu. Wydanie
  `artifacts/Dismode-App` zawiera właściwy plik, licencję i notices;
  rozmiar `956768` i SHA-256
  `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`
  są zgodne z przypiętym komponentem.
- Pełna regresja ujawniła dwa wcześniejsze, niezwiązane oczekiwania:
  `ApprovedZeroChangePlanLaunchesTracksAndWritesHistory` nadal szuka usuniętej
  pozycji planu `NO_SYSTEM_MUTATIONS`, a test niepełnego schematu SQLite
  oczekuje innego typu wyjątku. Nowe testy telemetrii i test ochrony ładowania
  bibliotek natywnych są zielone.
- Migrację opublikowano po potwierdzeniu braku aktywnej sesji, braku
  niedokończonych wpisów journalu i checkpointu recovery `10`. Smoke
  opublikowanego wydania potwierdził jeden podwyższony SessionHost, jeden
  SystemAgent, jedno niepodwyższone i zmaksymalizowane UI, oba gotowe pipe'y,
  288 procesów, 316 usług oraz brak aktywnej sesji. Kopia poprzedniego
  wydania: `Dismode-App.backup-20260729-195010`.
- Build Debug i Release zakończyły się bez ostrzeżeń; pełna regresja została
  celowo pominięta na żądanie użytkownika podczas tej iteracji.
- Wąski test produkcyjnej `RuntimeProcessEcoQosAction` na własnym process
  harnessie przeszedł 1/1: osobna transakcja zastosowała EcoQoS, journal
  umożliwił przywrócenie stanu, a powtórne recovery było idempotentne.
- Produkcyjna akcja priorytetu gry przeszła kontrolowany test 1/1:
  niezależny od akcji odczyt Windows zwrócił `High`, po czym recovery
  przywróciło `Normal`.
- Scenariusz launchera przeszedł 1/1: po jego wyjściu dziecko gry utrzymało
  aktywną sesję, przetrwało restart SessionHost i przyjęło łagodne żądanie
  zamknięcia; test lekkiego monitoringu i dotychczasowego recovery przeszedł
  dodatkowo 2/2.
- Parser CSV v2 PresentMon przeszedł 3/3 wąskie testy; oficjalny proces 2.5.1
  zwrócił prawdziwe wiersze `FrameTime`, a dołączony plik przeszedł kontrolę
  rozmiaru i SHA-256.
- Opublikowany `Dismode.exe` uruchomił dokładnie jeden UI, SessionHost i
  SystemAgent; po poprawce liczników pełne discovery zwróciło 287 procesów
  i 316 usług bez truncation, protokół v4 i brak aktywnej sesji. Bezpośredni
  odczyt Windows chwilę wcześniej zwrócił 286 procesów; dodatkowym procesem
  był uruchomiony klient diagnostyczny.
- Test kompletności inwentaryzacji usług przeszedł 1/1 i potwierdził, że
  żaden rekord zwracany przez `ServiceController.GetServices()` nie jest
  pomijany. Przekazany eksport zawierał 317 rekordów, bieżący Windows 316;
  jedyną nieobecną już usługą z eksportu była `Usługa pomocy ZTDNS`.
- Automatyczna biblioteka zachowała 9 aktualnych gier z Steam, Epic i Roblox;
  dostępny wyłącznie jako chroniony obraz Minecraft Launcher nie został
  błędnie dodany jako bezpośredni EXE, a SQLite potwierdził schemat v3.
- Skrót `C:\Users\Damia\Desktop\Dismode.lnk` wskazuje na istniejący
  `artifacts\Dismode-App\Dismode.exe`; wcześniejsze wydania są zachowane
  w katalogach `Dismode-App.backup-*`; kopia sprzed poprawki liczników to
  `Dismode-App.backup-20260729-135605`.
- Bieżące wydanie zawiera bezpieczne dołączanie do już uruchomionej gry oraz
  ochronę wszystkich pomocników z rozpoznanego katalogu instalacyjnego gry.
- Bieżące wydanie wystawia jawną regułę `BelowNormal + EcoQoS`, zachowując
  osobne opcje samego priorytetu, zamknięcia/przywrócenia i ignorowania.
- Bieżące wydanie pokazuje `High` jako rzeczywisty priorytet Windows i
  wyświetla FPS/ms tylko z danych PresentMon dla śledzonego PID.
- Rzeczywisty Roblox pozostał uruchomiony przez wcześniejszy proces
  aktualizacji i nie został restartowany w celu wymuszenia telemetrii.
- Gamingowy interfejs i ciemny pasek tytułu zostały opublikowane do
  `artifacts/Dismode-App`; build Release zakończył się bez ostrzeżeń,
  responsywne UI uruchomiło się z jednym oknem, a oba pipe'y zwróciły stan
  gotowy. Kopia poprzedniego wydania:
  `Dismode-App.backup-20260729-161503`.
- Klikalne przyciski Play zostały opublikowane do tego samego wydania.
  Build Debug i Release zakończyły się bez ostrzeżeń; journal miał 10
  zakończonych rekordów, a smoke opublikowanej aplikacji potwierdził jeden
  UI, gotowy SessionHost, SystemAgent `ReadOnly`, 257 procesów, 316 usług
  i brak aktywnej sesji. Nie uruchamiano prawdziwej gry w ramach walidacji.
  Kopia poprzedniego wydania:
  `Dismode-App.backup-20260729-191034`.

## Ryzyka i kruche miejsca

- Produkcyjne operacje na SCM i planach zasilania pozostają zablokowane przed testem własnej usługi i macierzą na kontrolowanej VM.
- Przy zewnętrznej zmianie aktywnego planu recovery zachowuje wybór użytkownika i raportuje konflikt; własny klon pozostaje wtedy do jawnej decyzji zamiast automatycznego usunięcia w stanie konfliktowym.
- IPC między procesem użytkownika i usługą jest granicą bezpieczeństwa, nie tylko transportem.
- WinUI 3 i Windows App SDK wymagają zgodności wersji SDK, target frameworku, packagingu i architektury.
- Standardowy `dotnet publish` nie kopiował luźnych zasobów WinUI i powodował
  awarię `0xc000027b`; target projektu UI sprawdza i rekursywnie kopiuje XBF
  wraz z aplikacyjnym PRI, a smoke test musi uruchamiać artefakt, nie sam
  build.
- Pierwsza inicjalizacja SQLite nie może rozpoczynać się synchronicznie na
  stosie zdarzenia WinUI `Loaded`: na Windows App SDK 2.3 kończyło to proces
  wyjątkiem XAML `0xc000027b`, mimo poprawnej bazy. `OnRootLoaded` najpierw
  oddaje jeden przebieg dispatcherowi przez `Task.Yield()`, a dopiero potem
  uruchamia pełną, niepomijaną sekwencję startową.
- Nie wolno wracać do pełnego SHA-256 w dwusekundowej pętli monitorowania. Pomiar przed poprawką wykazał około 4,4% CPU całej 16-wątkowej maszyny dla SessionHost; test `ActiveMonitorUsesLightweightRuntimeIdentityChecks` pilnuje lekkiej ścieżki.
- PresentMon może nie otrzymać zdarzeń ETW dla konkretnej gry. Status źródła
  musi pozostać widoczny, a wartości poza dwusekundowym oknem nie mogą być
  prezentowane jako aktualne FPS.
- Nakładka topmost działa nad trybem okienkowym i borderless. Ekskluzywny
  fullscreen może wymagać przełączenia gry na borderless.
- Po awarii SessionHost wynik wydajności obejmuje próbki dostępne po
  recovery; okresowy, niezależny checkpoint agregatu nie jest jeszcze
  zaprojektowany.
- Mechanizm recovery musi działać niezależnie od UI, SessionHost i głównej bazy.
- Testy na rzeczywistym systemie gospodarza nie mogą zastąpić testowej usługi i adapterów symulacyjnych.
- `Microsoft.Data.Sqlite 10.0.10` domyślnie zależał od podatnego natywnego SQLite; jawnie przypięto poprawione `SQLitePCLRaw.lib.e_sqlite3 2.1.12`, a audit restore jest ponownie czysty.
- Niepodpisany instalator może wywołać ostrzeżenie SmartScreen. Suma SHA-256
  potwierdza integralność, ale nie zastępuje docelowego podpisu Authenticode.

## Wydanie 0.2.0 — Bento UI dla całego interfejsu i uproszczenie widoków

- Wszystkie zakładki aplikacji (**Biblioteka**, **Tryb gry**, **Historia sesji**, **Aktualizacje**, **Diagnostyka i Ustawienia**) zostały zunifikowane w stylu nowoczesnego, przejrzystego **Bento Box UI**.
- Usunięto nieczytelne dla gracza techniczne szczegóły („geek clutter”), takie jak surowe hashe SID użytkownika, limity buforów IPC w kilobajtach, wielolinijkowe disclaimery czy dumpy ścieżek plików.
- Przebudowano kafelki diagnostyki Windows 11 (HAGS, Game Mode, Game DVR) i status modułów silnika Dismode.
- Kompilator Inno Setup wygenerował zaktualizowany pakiet instalatora: `artifacts/installer/Dismode-Setup-0.2.0-win-x64.exe` (SHA-256: `C7669AA8DBD842FC7530615B4043267D9FF4DF395B9600BD0AB3151530B388D6`, 70.29 MiB).
- Zestaw 150/150 testów automatycznych przeszedł w 100% z wynikiem pozytywnym.

## Wydanie 0.3.0 — niezależny Dismode Memory Optimizer

- `components/Dismode.MemoryOptimizer` jest osobnym rozwiązaniem GPL-3.0-only
  z izolowanymi projektami Core, LocalSystem Windows Service oraz
  niepodwyższonym WinUI 3/trayem. Nie ma referencji do zamkniętych bibliotek
  Dismode.
- Port operacji pamięci zachowuje osiem flag Windows Memory Cleaner 3.0.8,
  używa `LibraryImport`, bezpiecznych uchwytów, zakresowych uprawnień,
  mapowania `NTSTATUS`, anulowania, single-flight i raportów per operacja.
- Per-użytkownik IPC v1 wymusza ACL/SID, impersonację, limit 1 MiB,
  timestamp, replay protection i idempotency powiązane z hashem żądania.
  SQLite w `%ProgramData%` ma jednego writera w usłudze.
- Tray pozostaje aktywny po zamknięciu głównego Dismode. Główne UI eksportuje
  tylko neutralne pliki stanu gry i potrafi otworzyć osobny komponent.
  Protokół gamingowy v5 ma bezpieczną rezerwację `ShutdownComponents`, która
  nigdy nie zatrzymuje Memory Optimizer ani jego usługi.
- Instalator traktuje komponent jako domyślnie wybrany, opcjonalny agregat,
  zachowuje poprzedni wybór, pokazuje osobną informację GPL oraz rejestruje
  usługę delayed-auto i zadanie logowania dla każdej interaktywnej sesji;
  tray respektuje ustawienie startu z Windows osobno dla SID użytkownika.
- Awarię zainstalowanego interfejsu odtworzono i zdiagnozowano z pełnego dumpa:
  inicjalizacja WinRT `AccessibilitySettings` w polu okna kończyła WinUI
  fail-fastem `CLASS_E_CLASSNOTAVAILABLE`. Obsługa High Contrast korzysta teraz
  z `SystemParametersInfoW` i `GetSysColor`; finalny niewidoczny startup-probe
  kończy się kodem 0 i nie tworzy nowego zdarzenia crash.
- Główne UI rozróżnia teraz działającą usługę od zamkniętego interfejsu tray i
  raportuje przedwczesne zakończenie nowo uruchomionego procesu wraz z kodem.
  Ekran konserwacji instalatora używa wykrytej ścieżki istniejącej instalacji,
  więc akcja „Odinstaluj” nie rozwija `{app}` przed jego inicjalizacją.
  Manifest trayu deklaruje również `PerMonitorV2` i `longPathAware`, aby uniknąć
  bitmapowego skalowania na monitorach 4K/OLED. Pełny build Release obu
  rozwiązań zakończył się bez ostrzeżeń. Regresja Dismode: 217/217 (77 Unit,
  124 Integration, 13 Recovery, 3 Security). Regresja komponentu: 58/58 Core
  oraz 12/12 Security; jeden agresywny test VM został zgodnie z bramą pominięty.
  Oba przebiegi `dotnet format --verify-no-changes` są czyste.
- Ustawienia HUD mają niezależne przełączniki śledzenia FPS i nakładki. Po
  wyłączeniu śledzenia sesja zatrzymuje provider, czyści próbki i ukrywa HUD;
  po wyłączeniu samej nakładki pomiar może nadal działać. Stan jest trwały w
  migracji SQLite v11, a nowe polecenie IPC można włączać bez restartu sesji.
  Elementy informacji, postępu i niezapisanych zmian w Memory Optimizer mają
  zarezerwowane miejsce lub warstwę overlay, dzięki czemu akcje przycisków nie
  powodują chwilowego przeskoku całej strony.
- Payload `artifacts/Dismode-MemoryOptimizer` ma wersję 0.3.0.0, zero PDB,
  736 zweryfikowanych wpisów SHA-256, skrypty instalacji/deinstalacji, licencję,
  atrybucję i odpowiadające źródła. SHA-256 archiwum źródłowego:
  `22B799207B2CCCC4BA6F85953191AD033D660D7735DEBD86FB7484159EA36EED`.
- `Build-Installer.ps1 -SkipTests` potwierdził czysty journal (1127 rekordów),
  zweryfikował manifest głównego payloadu 703/703 i utworzył finalny instalator
  `Dismode-Setup-0.3.0-win-x64.exe` (163 143 906 B, SHA-256
  `0928FACF8DE43BBAC3B6AC3C96F4675865E54005D2C7B5EFF07A09FBD051DDCA`).
  Staging aktualizatora ma podpisany manifest preview 0.3.0, minimum 0.1.1
  i osiem chunków zgodnych z tym instalatorem; `Build-Installer.ps1` uruchamia
  publikator automatycznie.
  Authenticode instalatora pozostaje `NotSigned` (wymaga certyfikatu wydawcy).
  Nie uruchamiano instalatora, usługi ani agresywnych operacji na hoście.

## Otwarte pytania do ręcznej weryfikacji

- Ręcznie potwierdzić wizualną obecność „Uruchom przez Dismode” w głównym
  menu kontekstowym Windows 11 dla pliku `.exe`; rejestracja manifestu i
  aktywacja klasy COM zostały już potwierdzone automatycznie.
- Ręcznie ocenić nowy widok Bento i HUD 344×152 przy rzeczywistym DPI oraz
  zmierzyć czas kliknięcie Play → pojawienie się procesu/okna gry.
- Jakie środowisko VM będzie dostępne do testów instalacji usługi, crash injection i restart recovery?
- Po instalacji przejść pierwsze uruchomienie 0.1.2, aktualizację oraz trzy
  warianty deinstalacji z właściwym zachowaniem danych recovery.
- Czy po wersji badawczej potrzebny jest także podpisany MSI obok bieżącego,
  self-contained instalatora EXE?
- Jaka ma być docelowa nazwa wydawcy po uzyskaniu certyfikatu podpisu?
- Ręcznie potwierdzić tryb wysokiego kontrastu, skalowanie 300% i pełny przebieg Narratora; użytkownik przerwał bieżącą sesję sterowania UI klawiszem Esc.
- Przy następnej zwykłej sesji gry ręcznie potwierdzić kartę Historii z
  agregatem FPS/ms; nie uruchamiać gry wyłącznie w celu tej kontroli.
- Przy tej samej sesji potwierdzić wizualnie, że zmniejszenie „Krycia”
  odsłania obraz gry zamiast wygaszać zawartość HUD do czerni.

## Walidacja Windows po handoffie Linux — 2026-09-05

- Handoff z Linuxa został przyjęty jako punkt wyjścia dla bieżącego repozytorium;
  na Windows sprawdzono kod po zmianach OptiScaler, System Optimizer 0.4.0 i
  niezależnego Memory Optimizer bez sterowania pulpitem.
- Host: Windows 11 Insider x64, build `28120`; `.NET SDK 10.0.303`, Windows
  SDK `10.0.26100.0`. Restore i build Debug `Dismode.sln` oraz restore i
  build Release rozwiązania Memory Optimizer zakończyły się 0 ostrzeżeń i 0
  błędów.
- Bezpieczna regresja Release Dismode (z wyłączeniem testów uruchamiających
  okna/harness oraz globalnie mutujących cache): 134 Unit, 13 Recovery,
  140 Integration i 4 Security przeszły; jeden test symlinków został
  pominięty, bo host nie udostępnił wymaganej funkcji. Memory Optimizer:
  58 Core i 12 Security przeszło; jeden agresywny test VM pominięto bez
  `DISMODE_ALLOW_AGGRESSIVE_MEMORY_TESTS=1`. Oba rozwiązania przeszły
  `dotnet format --verify-no-changes`.
- Dedykowane testy OptiScaler na Windows: 17/17 Unit oraz 21/22 Integration;
  pominięty test symlinków jest tym samym kontrolowanym skipem hosta.
- Testy globalnego `SystemMemoryCleaner` mają teraz jawne zabezpieczenie VM;
  nie uruchamiają `SetSystemFileCacheSize` na zwykłym komputerze.
- Read-only smoke: journal recovery jest czysty (1127 rekordów), PresentMon
  2.5.1 ma stan `Ready`, a `Dismode.SystemAgent.exe --diagnostics` zwraca
  protokół v6. Zainstalowany komponent Memory Optimizer pozostaje uruchomiony
  jako `DismodeMemoryService` (LocalSystem) wraz z trayem.
- Zbudowano finalny lokalny staging 0.4.0. Instalator
  `artifacts\\installer\\Dismode-Setup-0.4.0-win-x64.exe` ma 223 888 088 B
  i SHA-256
  `26C6A83C5D870E12F8CFC272AA5BD528F88F10D5F71D51427435914475C70369`;
  Authenticode jest `Valid`, a 24 własne binaria payloadu mają wersję
  `0.4.0.0` i zgodny podpis. Manifest preview ma wersję `0.4.0`, minimum
  `0.3.0`, 11 chunków, a ich odtworzenie daje ten sam SHA-256.
- GPL release gate przeszedł. NOTICE wskazuje `Dismode.MemoryOptimizer
  0.4.0`, archiwum `Source/Dismode.MemoryOptimizer-0.4.0-source.zip` ma
  SHA-256 `7FFFDF98286946C588183251A5F3A28EB058A0196426B2347A746AF36032B8BF`,
  a wszystkie sześć własnych binariów Memory Optimizer jest podpisanych.
- Podpis użyty do lokalnego stagingu to certyfikat deweloperski
  `CN=Dismode Development`; pipeline produkcyjny nadal wymaga certyfikatu
  wydawcy. Instalatora nie uruchamiano, usługi SystemAgent nie instalowano,
  nie wykonywano restartu, gry ani agresywnej mutacji Windows.

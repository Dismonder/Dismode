# Plan wykonawczy: platforma aktualizacji i utwardzenie instalatora

Status: aktywny — 0.4.0 zbudowane, podpisane certyfikatem testowym
i opublikowane na kanale preview 2026-09-05; wydanie publiczne nadal wymaga
produkcyjnego certyfikatu Authenticode oraz update/uninstall smoke na VM
Utworzono: 2026-08-01

## Cel

Dostarczyć powtarzalną instalację Dismode na obsługiwanych komputerach oraz
bezpieczny kanał aktualizacji ręcznych i automatycznych. Aktualizacje mają być
sprawdzane automatycznie najwyżej raz na 30 dni i pobierane z publicznego,
tylko-do-odczytu Cloudflare Worker Static Assets. Instalator jest dzielony na
fragmenty poniżej limitu 25 MiB i instalowany wyłącznie po kryptograficznej
weryfikacji manifestu, każdego fragmentu oraz złożonej paczki.

## Granice bezpieczeństwa

- Aktualizator nigdy nie zamyka gry ani obcych procesów; może zamknąć wyłącznie
  komponenty Dismode z dokładnie zweryfikowanego katalogu instalacji.
- Aktualizacja, naprawa i pełna deinstalacja są blokowane przy aktywnej sesji
  gry, niedokończonym journalu lub oczekującym recovery.
- UI pozostaje `asInvoker`; podniesienie uprawnień następuje dopiero przy
  uruchomieniu zweryfikowanego instalatora do `Program Files`.
- Nie istnieje API wykonania dowolnej komendy, skryptu, URL ani EXE.
- Klient akceptuje wyłącznie HTTPS, znany schemat manifestu, dozwolony kanał,
  podpis ECDSA P-256/SHA-256, zgodny SHA-256 i rozmiar instalatora.
- Automatyczny check nie wysyła listy gier, procesów, sprzętu ani wyników FPS.
  Serwer otrzymuje wyłącznie standardowe metadane żądania HTTP.
- Wersja niższa od zainstalowanej lub niższa od minimalnej bezpiecznej wersji
  jest odrzucana; rollback wydania odbywa się przez zachowaną, wcześniej
  zweryfikowaną paczkę, a nie przez pobranie starszej wersji z manifestu.

## Architektura

```text
Dismode.UI (bez administratora)
  ├─ UpdateCheckService — manualnie lub co 30 dni
  ├─ SignedManifestVerifier — wbudowany klucz publiczny
  └─ UpdateStagingService — HTTPS + limit + SHA-256
             ↓ zweryfikowany update ticket
Dismode.Updater (minimalny helper)
  ├─ ponowna walidacja ticketu, manifestu i paczki
  ├─ brama active-session/recovery
  ├─ zamknięcie wyłącznie własnych komponentów
  └─ uruchomienie stałego instalatora z UAC

Cloudflare Worker + Static Assets (publiczny odczyt)
  ├─ /health
  ├─ /v1/channels/{channel}/manifest.json
  └─ /v1/packages/{version}/{filename}.part-NNNN.bin

Publikacja (lokalnie/CI)
  ├─ self-contained installer
  ├─ SBOM + third-party notices
  ├─ podpis manifestu kluczem prywatnym poza repo
  └─ upload przez Wrangler; Worker nie ma endpointu zapisu
```

## Etapy

### 1. Kontrakt i serwer Cloudflare

Status: ukończone

- utworzyć Worker TypeScript ze statycznymi assetami na planie Free;
- dzielić duże EXE na fragmenty poniżej 25 MiB;
- ograniczyć ścieżki do zamkniętego schematu kanałów i paczek;
- dodać lokalne testy runtime, typecheck i dry-run deploy;
- sprawdzić autoryzację Wrangler i wdrożyć bezpłatne środowisko.

Zrealizowano:

- Worker działa pod
  `https://dismode-update-service-dev.dismonder.workers.dev`;
- typecheck, 5/5 testów runtime oraz dry-run zakończyły się kodem 0;
- live `/health` i strona produktu zwracają 200, a zapis zwraca 405;
- R2 nie jest używane. Konto zwróciło kod 10042 wymagający aktywacji przez
  ekran subskrypcji, dlatego ta zależność została usunięta z architektury.

### 2. Podpisany manifest i publikacja

Status: implementacja ukończona; 0.1.1 opublikowane, finalne 0.1.2 oczekuje

- wersjonowany, kanoniczny JSON bez pola podpisu w signed payload;
- ECDSA P-256/SHA-256; klucz prywatny tylko poza repo;
- SHA-256 i rozmiar instalatora, kanał, minimalna wersja, data i release notes;
- skrypt budowy, podpisu, weryfikacji oraz uploadu;
- atomowa publikacja: najpierw niezmienna paczka, manifest jako ostatni.

### 3. Klient aktualizacji

Status: ukończone

- SQLite v6: kanał, auto-update, ostatnia udana próba, ostatni błąd i wersja;
- ręczny check zawsze dostępny; automatyczny najwyżej raz na 30 dni;
- limit rozmiaru manifestu, timeout, HTTPS, ETag i uczciwe statusy offline;
- staging w `%LocalAppData%\\Dismode\\Updates`;
- UI Bento: stan wersji, check, pobranie, instalacja, preferencje i prywatność.

### 4. Bezpieczny handoff i mutexy

Status: ukończone dla instalatora 0.1.2

- pojedyncza instancja UI i setupu;
- minimalny `Dismode.Updater.exe` z zamkniętym ticketem;
- brama aktywnej sesji i recovery przed zamykaniem czegokolwiek;
- zamknięcie tylko znanych procesów Dismode z katalogu instalacji;
- uruchomienie instalatora i potwierdzenie zdrowego startu nowej wersji;
- zachowanie poprzedniej zweryfikowanej paczki do potwierdzenia.

### 5. Instalator i dokumentacja prawna

Status: implementacja ukończona; smoke czystej instalacji zablokowany przez UAC

- pełne metadane Technical Preview i rzeczywiste URL-e produktu/pomocy/update;
- licencja/warunki, prywatność, diagnostyka, telemetria i third-party notices;
- self-contained payload, weryfikacja manifestu plików i tryb naprawy;
- fallback ToolHelp, gdy WMI nie działa;
- trzy warianty odinstalowania, przy czym recovery zawsze pozostaje do czasu
  potwierdzonego przywrócenia zmian.

### 6. Łańcuch wydania

Status: częściowo ukończone; finalny deploy 0.1.2 oczekuje na smoke instalacji

- CycloneDX SBOM, audit zależności, usunięcie PDB z instalatora;
- build/test/format, weryfikacja self-contained na czystym środowisku;
- dry-run Wrangler, deploy Worker i publikacja pierwszego manifestu;
- dokumentacja ręcznego rollbacku i checklisty UAC/SmartScreen.

## Dowody walidacji

- Finalny build 0.1.2: kod 0, 0 ostrzeżeń, format bez zmian, czysty journal
  (797 rekordów), 106/106 testów.
- `Dismode-Setup-0.1.2-win-x64.exe`: 71 728 335 B, SHA-256
  `F4CC1136A92BC68622CFEC00247F9DA089F87C5266407AFA7D9C35EB82952742`.
- Payload: 0 PDB, 0 Windows Forms, self-contained, wersje plików 0.1.2.0.
- Natywna DLL menu eksportuje oba entrypointy COM. Podpis sparse MSIX i
  dołączony publiczny certyfikat mają identyczny thumbprint.
- Czysta instalacja i hotfix zakończyły się kodem 0. Pakiet sparse ma stan
  `Ok`, zainstalowane hashe są zgodne z payloadem, COM menu aktywuje się,
  UI jest responsywne i zmaksymalizowane, oba hosty odpowiadają przez IPC.

## Wydanie 0.4.0 na kanale preview — 2026-09-05

Wydanie prywatne, przeznaczone do testów na drugim komputerze. Podpisane
certyfikatem testowym `CN=Dismode Development`, odcisk
`B78DD0E0D609D24588608BB76AF87F007ADC63A5`.

- `Dismode-Setup-0.4.0-win-x64.exe`, 223 988 960 B (213,61 MiB),
  SHA-256 `40CC1DD41749A450FC7AF8884BE5E112287D88D8E5C6DE40B3CF9DF42C8AD5E9`,
  podpis Authenticode `Valid`, build zakończony kodem 0.
- Testy przed spakowaniem: UnitTests 180/180, RecoveryTests 13/13,
  SecurityTests 9/9, IntegrationTests 183/186 (3 pominięte wymagają admina).
  `dotnet format --verify-no-changes` bez zmian.
- Worker: typecheck czysty, 18/18 testów runtime, dry-run i deploy kodem 0.
  Wersja wdrożenia `c0949ff2-fb22-456a-af44-2e70f0f9e987`.
- Manifest kanału `preview` serwuje 0.4.0 z `minimumSupportedVersion` 0.1.1,
  11 fragmentów, podpis ECDSA obecny.
- Weryfikacja end-to-end z publicznego URL: wszystkie 11 fragmentów pobrane,
  każdy o zgodnym rozmiarze i SHA-256; skrót złożonej paczki identyczny
  z lokalnie zbudowanym instalatorem.

Warunek konieczny na maszynie docelowej: certyfikat testowy musi trafić do
`Trusted Root Certification Authorities` oraz `Trusted Publishers`. Bez tego
`WinVerifyTrust` odrzuci podpis, więc SystemAgent potraktuje SystemOptimizer
jako niezaufanego klienta i zablokuje mutacje, a instalator zobaczy
ostrzeżenie SmartScreen. Część publiczna certyfikatu leży w
`artifacts/installer/Dismode-TestSigning-B78DD0E0.cer`.

## Wydanie 0.4.1 na kanale preview — 2026-09-05

Wydanie naprawcze deinstalatora. Numer podbity, bo zmienia zachowanie, a
publikowanie innej binarki pod istniejącym 0.4.0 złamałoby niezmienność paczki.

- `Dismode-Setup-0.4.1-win-x64.exe`, 223 993 976 B (213,62 MiB),
  SHA-256 `E383CC6478FCD3E9C4CCE5A8918B79097F8C136BABB2163798A68CC1691E06E0`,
  podpis Authenticode `Valid`, build kodem 0.
- Kanał `preview` serwuje 0.4.1 z `minimumSupportedVersion` 0.1.1.
  Wersja wdrożenia Workera `dfa73633-397f-4947-bc46-25b2ba6fa98d`.
- Paczka 0.4.0 pozostaje dostępna pod swoim adresem — niezmienność wydań
  zachowana, zmienił się wyłącznie manifest kanału.
- Podbicie wersji wymagało aktualizacji trzech testów przypinających numer.
  To celowa bramka: zmiana wersji nie może przejść niezauważona.

## Aktualizacje różnicowe — zbadane i odrzucone 2026-09-05

Pytanie: czy da się zmniejszyć 213 MiB pobierania przy aktualizacji.
Odpowiedź: nie w obecnym kształcie pipeline'u. Poniżej pomiary, nie szacunki.

### Pomiar 1: limit MSDelta

`msdelta.dll` to natywne API Windows (to samo, którego używa Windows Update),
dostępne bez żadnej nowej zależności. Ma jednak twardy limit rozmiaru:

| Rozmiar wejścia | Wynik `CreateDeltaW` |
|---|---|
| 2 MB | OK, delta 933 B przy 500 zmienionych bajtach |
| 32 MB | OK, delta 31 B przy 1 zmienionym bajcie |
| 64 MB | `ERROR_INVALID_DATA` (13) |
| 128 MB | `ERROR_INVALID_DATA` (13) |

Instalator ma 213 MiB, więc delta całego pliku odpada. Fragmenty kanału mają
20 MiB i mieszczą się w limicie, więc zbadano wariant per fragment.

### Pomiar 2: determinizm builda

Przebudowano 0.4.0 **bez żadnej zmiany w kodzie** i porównano z opublikowanym
artefaktem fragment po fragmencie (20 MiB, tak jak w kanale):

- rozmiary różnią się: 223 988 960 B wobec 224 046 216 B;
- **żaden z 11 fragmentów nie jest identyczny**;
- suma delt: 193 453 694 B wobec 224 046 216 B pełnego pobrania;
- oszczędność: **13,7 %**;
- cztery fragmenty mają deltę *większą* niż sam fragment, czyli MSDelta nie
  znalazła w nich żadnego podobieństwa i dołożyła narzut.

To jest górna granica możliwości. Identyczne źródło to najlepszy możliwy
przypadek — prawdziwe wydanie ze zmianami wypadnie gorzej.

### Wniosek

Aktualizacje różnicowe nie mają sensu, dopóki nie zmieni się fundament:

1. Build nie jest deterministyczny. Ten sam kod daje inne bajty, więc nawet
   niezmieniona część payloadu trafia do paczki jako „zmieniona".
2. Inno Setup kompresuje payload jednym, solidnym strumieniem LZMA. Dowolna
   różnica przesuwa cały strumień od tego miejsca, więc podobieństwo
   pozycyjne znika niezależnie od użytego algorytmu delty.

Zbudowanie tego teraz dałoby duży, wrażliwy na błędy podsystem
(nowy format paczki, retencja poprzedniego instalatora ~213 MiB na dysku,
ścieżka awaryjna, dodatkowa powierzchnia weryfikacji podpisów) w zamian za
kilkanaście procent w najlepszym przypadku.

### Co zamiast tego

Payload nieskompresowany waży 817,6 MB, z czego **549,3 MB to duplikaty** —
te same pliki w czterech niezależnych katalogach self-contained:

| Plik | Kopie | Zmarnowane |
|---|---|---|
| `Microsoft.Windows.SDK.NET.dll` (25,1 MB) | 4 | ~75 MB |
| `onnxruntime.dll` (20,7 MB) | 3 | ~41 MB |
| `DirectML.dll` (17,8 MB) | 3 | ~36 MB |

`onnxruntime.dll` i `DirectML.dll` pochodzą z Windows App SDK 2.3.1 i służą
Windows ML, którego Dismode nie używa.

Deduplikacja payloadu i usunięcie nieużywanego runtime'u ML zmniejszy każde
pobranie — również świeżą instalację — bez nowej powierzchni bezpieczeństwa
i bez trzymania kopii poprzedniego instalatora na dysku użytkownika.

## Defekt: deinstalator nie do przejścia — zgłoszony i naprawiony 2026-09-05

Zgłoszenie z drugiego komputera: deinstalator przerwał pracę komunikatem
o System Optimizer i niepotwierdzonym restore, **zostawiając obie usługi na
autostarcie**. Użytkownik sprawdził dane recovery — wszystkie 39 rekordów
dotyczyło wyłącznie priorytetów procesów gier, których procesy dawno nie
istnieją. Priorytet procesu nie jest trwały, więc nie było czego przywracać.
Usunięcie ręczne było bezpieczne.

### Analiza

Objaw był łagodniejszy niż przyczyna. Defekt ma trzy warstwy:

1. **Usuwanie usług było schowane za powodzeniem bramy.** W
   `InitializeUninstall` wywołania `RunShellIntegrationScript`
   i `UninstallSystemAgentService` stały za `if Result then`. Gdy brama
   zwracała błąd, Inno przerywał deinstalację, a usługi zostawały
   zarejestrowane i uruchamiane przy starcie systemu.
2. **Brak jakiegokolwiek wyjścia.** Komunikat szedł przez `MsgBox(..., MB_OK)`,
   więc nie istniała ścieżka „usuń mimo to". Ślepy zaułek.
3. **Zamknięta pętla na buildach bez zaufanego podpisu.** Brama żąda
   `RestoreAllAsync`, czyli mutacji. `SystemOptimizerCallerPolicy` odrzuca
   mutacje od klientów bez zaufanego podpisu Authenticode. Na maszynie,
   gdzie certyfikat nie jest zaufany, restore **nie może** się udać, więc
   program nie dawał się odinstalować własnym deinstalatorem. To dotyczyło
   każdego wydania podpisanego certyfikatem testowym przed jego importem.

Warstwa 3 jest istotna także dlatego, że jeśli agent odrzucał mutacje, to
żadna trwała zmiana systemu nigdy nie została zastosowana — więc żądanie
restore było nie tylko niewykonalne, ale i bezprzedmiotowe.

### Naprawa

- `RecoveryJournalInspection` rozróżnia teraz zmiany **trwałe** od
  przejściowych: `PendingDurableActionIds` zbiera akcje, które osiągnęły
  `ActionApplied`/`ActionVerified` i nie mają odpowiadającej kompensacji.
  Journal zawierający wyłącznie pracę przejściową nie ma czego przywracać.
- Brama w `SessionHostUpdatePreparation` przepuszcza deinstalację, gdy
  journal maszynowy nie wykazuje żadnej trwałej zmiany oczekującej na
  cofnięcie. Nie żąda mutacji, która i tak zostałaby odrzucona.
- `InitializeUninstall` przy nieudanej bramie pokazuje pełną informację
  i pyta `MB_YESNO`, zamiast kończyć błędem. Dane recovery nie są kasowane
  przez deinstalację, więc zgoda niczego nie niszczy.
- Kroki porządkowe wykonują się niezależnie od siebie. Awaria jednego nie
  pomija pozostałych, a to, czego nie udało się usunąć, jest wypisane wraz
  z poleceniem ręcznego usunięcia usługi.

### Weryfikacja

- 6 nowych testów w `Dismode.RecoveryTests` przypina, co liczy się jako
  trwała zmiana: akcja zastosowana bez kompensacji blokuje, akcja
  skompensowana lub zablokowana przed zastosowaniem nie blokuje.
- Test kontraktowy `UninstallRestoresMachineTweaksBeforeRemovingSystemAgent`
  sprawdza teraz intencję, nie literalne brzmienie kodu: kolejność bramy
  przed usunięciem usługi, istnienie świadomego wyboru oraz to, że usuwanie
  usługi nie jest zależne od powodzenia bramy.
- `ISCC` kompiluje skrypt bez błędów i ostrzeżeń.
- UnitTests 180/180, RecoveryTests 19/19, SecurityTests 9/9,
  IntegrationTests 183/186, `dotnet format` bez zmian.

### Pozostaje

Naprawa jest w kodzie, ale **nie w opublikowanym 0.4.0**. Wymaga nowego
builda i sprawdzenia pełnej deinstalacji na maszynie testowej — w tym
scenariusza z niezaufanym podpisem, który ujawnił defekt.

## Ryzyka i decyzje otwarte

- Inno Setup bez ostrzeżeń kompiluje opcjonalny komponent GPL, zachowuje jego
  wybór przy aktualizacji i zawiera osobną informację licencyjną. Payload
  Memory Optimizer 0.3.0 przeszedł bramę licencji/źródeł/SHA-256 i nie zawiera
  PDB.
- Finalny agregat nie został oznaczony jako wydanie: `Build-Installer.ps1`
  przerwał na niedokończonym wpisie recovery
  `f34113f1-6660-4745-a726-d04a59fcf537`. Instalacja usługi i smoke na hoście
  pozostają do wykonania dopiero po bezpiecznym zamknięciu tej sesji.

- `Build-Installer.ps1` dla 0.4.0 wymaga produkcyjnego certyfikatu
  Authenticode, podpisuje własne klienty mutacji i instalator oraz weryfikuje
  allow-listę. Bez certyfikatu build kończy się fail-closed przed utworzeniem
  artefaktu.
- Kanał używa wyłącznie Workers Static Assets na planie Free. R2 nie należy
  do projektu i nie może stać się warunkiem publikacji.
- Bieżący lokalny journal zawiera niedokończoną sesję, dlatego finalny build,
  instalacja i publikacja pozostają zablokowane do jej bezpiecznego
  zakończenia lub recovery.
- Lista narzędzi open source będzie wdrażana etapami. Nie dodajemy bibliotek,
  dopóki konkretna funkcja ich nie używa i nie uzasadnia kosztu dystrybucji.

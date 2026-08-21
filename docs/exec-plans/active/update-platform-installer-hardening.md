# Plan wykonawczy: platforma aktualizacji i utwardzenie instalatora

Status: aktywny — instalacja i smoke 0.1.2 ukończone;
oczekuje na publikację kanału 0.1.2  
Utworzono: 2026-08-01

## Cel

Dostarczyć powtarzalną instalację GameShift na obsługiwanych komputerach oraz
bezpieczny kanał aktualizacji ręcznych i automatycznych. Aktualizacje mają być
sprawdzane automatycznie najwyżej raz na 30 dni i pobierane z publicznego,
tylko-do-odczytu Cloudflare Worker Static Assets. Instalator jest dzielony na
fragmenty poniżej limitu 25 MiB i instalowany wyłącznie po kryptograficznej
weryfikacji manifestu, każdego fragmentu oraz złożonej paczki.

## Granice bezpieczeństwa

- Aktualizator nigdy nie zamyka gry ani obcych procesów; może zamknąć wyłącznie
  komponenty GameShift z dokładnie zweryfikowanego katalogu instalacji.
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
GameShift.UI (bez administratora)
  ├─ UpdateCheckService — manualnie lub co 30 dni
  ├─ SignedManifestVerifier — wbudowany klucz publiczny
  └─ UpdateStagingService — HTTPS + limit + SHA-256
             ↓ zweryfikowany update ticket
GameShift.Updater (minimalny helper)
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
  `https://gameshift-update-service-dev.dismonder.workers.dev`;
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
- staging w `%LocalAppData%\\GameShift\\Updates`;
- UI Bento: stan wersji, check, pobranie, instalacja, preferencje i prywatność.

### 4. Bezpieczny handoff i mutexy

Status: ukończone dla instalatora 0.1.2

- pojedyncza instancja UI i setupu;
- minimalny `GameShift.Updater.exe` z zamkniętym ticketem;
- brama aktywnej sesji i recovery przed zamykaniem czegokolwiek;
- zamknięcie tylko znanych procesów GameShift z katalogu instalacji;
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
- `GameShift-Setup-0.1.2-win-x64.exe`: 71 728 335 B, SHA-256
  `F4CC1136A92BC68622CFEC00247F9DA089F87C5266407AFA7D9C35EB82952742`.
- Payload: 0 PDB, 0 Windows Forms, self-contained, wersje plików 0.1.2.0.
- Natywna DLL menu eksportuje oba entrypointy COM. Podpis sparse MSIX i
  dołączony publiczny certyfikat mają identyczny thumbprint.
- Czysta instalacja i hotfix zakończyły się kodem 0. Pakiet sparse ma stan
  `Ok`, zainstalowane hashe są zgodne z payloadem, COM menu aktywuje się,
  UI jest responsywne i zmaksymalizowane, oba hosty odpowiadają przez IPC.

## Ryzyka i decyzje otwarte

- Bez certyfikatu Authenticode Windows nadal może pokazać „Nieznany wydawca”;
  podpis manifestu chroni kanał GameShift, ale nie zastępuje Authenticode.
- Kanał używa wyłącznie Workers Static Assets na planie Free. R2 nie należy
  do projektu i nie może stać się warunkiem publikacji.
- Bieżący lokalny journal jest czysty. Publikacja 0.1.2 może przejść po
  zaktualizowaniu manifestu kanału do finalnego hasha hotfixu.
- Lista narzędzi open source będzie wdrażana etapami. Nie dodajemy bibliotek,
  dopóki konkretna funkcja ich nie używa i nie uzasadnia kosztu dystrybucji.

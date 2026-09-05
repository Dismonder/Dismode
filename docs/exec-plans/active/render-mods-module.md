# Plan wykonawczy: DLSS 5 i Neural Rendering w module OptiScaler

Status: aktywny — etapy 1-4 ukończone; etap 5 w toku, sprzęt potwierdzony,
pełny test w grze zablokowany zbyt starym sterownikiem NVIDIA
Utworzono: 2026-09-05

## Cel

Rozszerzyć istniejący moduł OptiScaler tak, aby dla profilu gry można było
włączyć nie tylko upscaling, ale też DLSS 5 z Neural Rendering — z zachowaniem
obecnej, transakcyjnej ścieżki powrotu.

## Stan wyjściowy

Moduł OptiScaler już istnieje i nie wymaga przebudowy:

| Element | Plik | Co robi |
|---|---|---|
| polityka bezpieczeństwa | `src/GameShift.Core/OptiScaler/OptiScalerSafetyPolicy.cs` | bramki gry uruchomionej, anti-cheatu i potwierdzenia offline; mapowanie proxy DLL |
| źródło paczek | `src/GameShift.Windows/OptiScaler/GitHubOptiScalerPackageSource.cs` | GitHub Releases dla kanałów stable/beta/nightly, allow-lista hostów, weryfikacja SHA-256 |
| ekstraktor | `src/GameShift.Windows/OptiScaler/SharpCompressOptiScalerArchiveExtractor.cs` | rozpakowanie archiwum |
| manager | `src/GameShift.Windows/OptiScaler/OptiScalerManager.cs` | transakcyjny deploy, kopie zapasowe, manifest, wykrywanie obcych modyfikacji, deinstalacja z rollbackiem |

Czego brakuje do DLSS 5:

- kanał wskazujący fork z Neural Rendering;
- pozyskanie `nvngx_dlssnr.dll` i pozostałych `nvngx_dls*.dll` ze sterownika;
- patch `OptiScaler.ini` — moduł dziś wgrywa INI z paczki bez zmian;
- bramki sprzętowe: GeForce RTX 50+, sterownik 616.56+, obecność modelu.

## Granice bezpieczeństwa

Obowiązują wszystkie dotychczasowe bramki modułu. Dochodzą trzy zasady:

- Pliki NVIDII pochodzą wyłącznie z DriverStore tej maszyny i muszą mieć ważny
  podpis Authenticode NVIDIA Corporation. Moduł ich nie pobiera z internetu
  i nie redystrybuuje.
- Wydanie forka jest przypięte skrótem SHA-256 w kodzie. Moduł nie instaluje
  „najnowszego" wydania forka automatycznie — nowa wersja wymaga zmiany kodu
  i przejścia testów.
- Patch INI zmienia wyłącznie zamkniętą listę kluczy. Wartości pochodzą z enum,
  nigdy z tekstu wpisanego przez użytkownika.

## Decyzje projektowe

### Nie redystrybuujemy binariów

Instalator GameShift nie zawiera ani OptiScalera, ani DLL-ek NVIDII.
OptiScaler jest na GPL-3.0, a pliki `nvngx_*` są własnością NVIDII.
Moduł pozyskuje jedno i drugie dopiero w chwili instalacji:

| Plik | Źródło | Weryfikacja |
|---|---|---|
| paczka OptiScaler DLSSNR | GitHub Releases, przypięty tag | SHA-256 przypięty w kodzie |
| `nvngx_dlssnr.dll` (~158 MB) | DriverStore użytkownika | podpis NVIDIA |
| `nvngx_dlss.dll`, `nvngx_dlssd.dll`, `nvngx_dlssg.dll` | DriverStore użytkownika | podpis NVIDIA |

Konsekwencja: nie da się wymusić nowszego DLSS niż ma zainstalowany sterownik.
To akceptowalne — podmiana na wersje spoza sterownika bywa łamana przez
aktualizacje NVIDII, a moduł ma być odwracalny i przewidywalny.

### Fork jako osobny kanał, przypięty

`Dagherbou/OptiScaler_DLSSNR`, wydanie `v0.2.0-dlssnr` z 2026-09-03, SHA-256
`8EECE7A4D7DE6DE5917F0C99AC60540B2D77022E7699BBA717B0A6D9E1829BCE`,
VirusTotal 0/66. Kanał jest oznaczony jako eksperymentalny, więc dziedziczy
istniejące wymaganie `ExperimentalUseConfirmed`.

Fork ma dwa dni i jednego autora, a binarka nie jest reprodukowalna z kodu.
Zaufanie opiera się na przypiętym skrócie, nie na dowodzie poprawności — i tak
trzeba to napisać w UI przed pierwszą instalacją.

### Warstwy i bramki sprzętowe

| Warstwa | Wymaganie | Zachowanie bez wymagania |
|---|---|---|
| OptiScaler + FSR/XeSS | dowolne GPU DX12 | dostępna |
| DLSS Super Resolution | GeForce RTX 20+ | pozycja nieaktywna z powodem |
| DLSS Neural Rendering | GeForce RTX 50+, sterownik 616.56+, model w DriverStore | pozycja nieaktywna z powodem |

Maszyna deweloperska ma AMD Radeon RX 9070 XT, więc ścieżek DLSS nie da się tu
zweryfikować end-to-end. Etapy 1–3 są testowalne jednostkowo; potwierdzenie
w grze wymaga maszyny z GeForce RTX 5070.

## Etapy

### 1. Bramki sprzętowe i kanał forka

Status: ukończone

- `OptiScalerReleaseChannel.DlssNeuralRendering` z przypiętym wydaniem;
- `GpuCapability` — rodzina GeForce, generacja, wersja sterownika;
- rozszerzenie `OptiScalerSafetyPolicy` o powody `GpuNotSupported`,
  `DriverTooOld`, `NeuralRenderingModelMissing`;
- testy jednostkowe wszystkich kombinacji bramek.

### 2. Pozyskanie plików NVIDII z DriverStore

Status: ukończone

- `NvidiaDriverStoreProbe` — lokalizacja `nv_dispi.inf_amd64_*`, wersja
  sterownika, obecność `nvngx_dls*.dll`;
- weryfikacja podpisu Authenticode przez istniejący
  `AuthenticodeSignatureVerifier`;
- dołączenie znalezionych plików do payloadu instalacji;
- testy na syntetycznym drzewie katalogów.

### 3. Patch OptiScaler.ini

Status: ukończone

- minimalny patch zamkniętej listy kluczy z zachowaniem CRLF i komentarzy:
  `Dx12Upscaler=dlss`, `Dx11Upscaler=dlss_12`, `VulkanUpscaler=dlss`,
  `[DLSS] Enabled=true`, `[DlssNr] Enabled=true`;
- parametrów modelu (`Preset`, `Style`, `Intensity`, `TransferStrength`)
  nie ruszamy — od strojenia jest overlay w grze;
- testy: patch idempotentny, nie gubi nieznanych kluczy, zachowuje końce linii.

### 4. Wpięcie w instalację i UI profilu gry

Status: ukończone

Zrealizowano:

- `OptiScalerInstallRequest.EnableNeuralRendering` steruje całą ścieżką;
- bramka sprzętowa działa **przed** pobraniem czegokolwiek, więc odrzucenie
  nie generuje ruchu sieciowego ani plików tymczasowych;
- pliki z DriverStore trafiają do payloadu i mają pierwszeństwo przed plikami
  o tej samej nazwie z archiwum, dzięki czemu model zawsze pasuje do sterownika;
- `OptiScaler.ini` jest patchowany z zachowaniem BOM UTF-8 i CRLF; brak
  któregokolwiek z wymaganych kluczy przerywa instalację przed zapisem;
- wydanie forka jest przypięte wersją `0.2.0-dlssnr` i skrótem SHA-256;
  filtr assetu jest zależny od kanału, bo fork publikuje `.zip`, a nie `.7z`;
- w oknie OptiScaler doszedł kanał „DLSS 5 Neural Rendering — fork",
  przełącznik Neural Rendering oraz opis wykrytego GPU i sterownika;
  przełącznik jest nieaktywny z konkretnym powodem, gdy sprzęt nie spełnia
  wymagań.

Świadomie pominięte:

- twarde dowiązanie zamiast kopii dla `nvngx_dlssnr.dll` (~158 MB).
  Kopiowanie jest wolniejsze i zajmuje miejsce, ale działa niezależnie od tego,
  czy katalog gry leży na tym samym woluminie co DriverStore. Optymalizacja
  ma sens dopiero, gdy okaże się realnie uciążliwa.

### 5. Weryfikacja na maszynie z RTX 5070

Status: w toku — sprzęt potwierdzony, sterownik blokuje pełny test

### Ustalenia z maszyny docelowej 2026-09-05

| Warunek | Wartość | Wynik |
|---|---|---|
| Karta | `NVIDIA GeForce RTX 5070` → `Rtx50` | spełniony |
| Sterownik | `32.0.16.1088` → **610.88** | **niespełniony**, wymagane 616.56 |

Windows raportuje wersję czteroczłonowo; przeliczenie na numerację NVIDII
wykonano prawdziwym kodem modułu (`NvidiaDriverVersion`), nie ręcznie.
Data sterownika 22.07.2026 jest sprzed premiery DLSS 5, więc brak modelu
`nvngx_dlssnr.dll` jest spodziewany — ale **nie został jeszcze potwierdzony
odczytem** z DriverStore tamtej maszyny.

Konsekwencja: kroki 3-5 runbooka (instalacja moda, test w grze) nie mogą
przejść przed aktualizacją sterownika. Wykonalne teraz są kroki 1-2 oraz
weryfikacja gałęzi odmowy — czy moduł zgłasza dokładnie `DriverTooOld`
z czytelnym komunikatem, zamiast mylącego błędu. Tego na Radeonie zbadać
się nie da, więc jest to realny zysk z dostępu do tej maszyny.

Próg 616.56 pochodzi z ustaleń sesji „DLL5 dla RTX5070" i nie został
niezależnie zweryfikowany w dokumentacji NVIDII. Jeśli okaże się
nieprecyzyjny, poprawka to jedna stała `MinimumDriverVersion`.

Runbook do wykonania na komputerze z GeForce RTX 5070:

1. **Warunki wstępne.** Sterownik NVIDIA 616.56 lub nowszy. Sprawdź, czy
   `C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_*`
   zawiera `nvngx_dlssnr.dll`. Bez tego pliku moduł zablokuje się z powodem
   `NeuralRenderingModelMissing` — i to też jest poprawny wynik testu.
2. **Rozpoznanie sprzętu.** Otwórz okno OptiScaler dla profilu gry i wybierz
   kanał „DLSS 5 Neural Rendering — fork". Opis pod przełącznikiem musi pokazać
   `Rtx50` oraz rzeczywistą wersję sterownika w formacie NVIDIA, na przykład
   `616.56`, a nie czteroczłonowy numer Windows.
3. **Instalacja.** Gra jednoosobowa na DX12, bez anti-cheatu. Zaznacz oba
   potwierdzenia i przełącznik Neural Rendering, zainstaluj.
4. **Weryfikacja plików.** W katalogu gry muszą pojawić się `nvngx_dlssnr.dll`
   oraz proxy DLL. W `OptiScaler.ini` sprawdź `[DlssNr] Enabled=true` oraz
   `Dx12Upscaler=dlss`. Potwierdź, że plik nadal zaczyna się od BOM
   (`Format-Hex -Count 3` musi dać `EF BB BF`).
5. **Test w grze.** Uruchom grę, `Insert`, sekcja „DLSS Neural Rendering".
   Pole „Enable Neural Rendering" musi być dostępne. Jeśli nie — powód
   pokaże się pod checkboxem, a szczegóły w `OptiScaler.log`.
6. **Deinstalacja.** Usuń modyfikację z GameShift i potwierdź, że katalog gry
   wrócił do stanu sprzed zmiany: nadpisane pliki mają oryginalną treść,
   a pliki dodane przez moduł zniknęły.
7. **Wykrywanie obcych zmian.** Zainstaluj ponownie, ręcznie zmodyfikuj jeden
   z zainstalowanych plików, po czym spróbuj usunąć. Moduł musi odmówić
   i wskazać zmieniony plik zamiast go skasować.

## Defekty wykryte podczas instalacji na maszynie docelowej 2026-09-06

Pierwsza próba użycia kanału forka na `ERYK` zakończyła się komunikatem
„Nie udało się zainstalować OptiScaler: Pobrany plik nie jest archiwum 7z".
Analiza wykazała **dwa niezależne błędy**, oba wprowadzone przy etapie 4.

### 1. Kontrola formatu archiwum zaszyta na 7z

`GitHubOptiScalerPackageSource` sprawdzał bajty magiczne
`37 7A BC AF 27 1C` niezależnie od kanału, a nazwa pliku w cache miała
zaszyte rozszerzenie `.7z`. Przy etapie 4 poprawiono wyłącznie filtr **nazwy**
assetu, nie kontrolę **zawartości**. Fork publikuje ZIP (`50 4B 03 04`), więc
poprawna, zweryfikowana paczka była odrzucana jako uszkodzona.

Kolejność kontroli okazała się pouczająca: SHA-256 sprawdzane jest **przed**
formatem, a przeszło. Oznacza to, że przypięty skrót forka jest prawidłowy
i paczka nienaruszona — zawiódł wyłącznie mój błędny warunek.

Naprawa: format archiwum zależy od kanału (`GetArchiveSignature`,
`GetArchiveExtension`) obok istniejącego filtru nazwy.

### 2. Filtr payloadu gubił cały runtime forka

`BuildPayload` przepuszczał pliki `.dll`/`.ini` z katalogu głównego oraz
katalogi `D3D12_Optiscaler\` i `Licenses\`. Fork ma inny układ — wszystkie
backendy leżą w `OptiScaler\`:

| Plik | Rozmiar |
|---|---|
| `OptiScaler/libxess.dll` | 77,8 MB |
| `OptiScaler/amd_fidelityfx_framegeneration_dx12.dll` | 40,1 MB |
| `OptiScaler/amd_fidelityfx_upscaler_dx12.dll` | 28,8 MB |
| `OptiScaler/libxess_fg.dll` | 23,0 MB |
| `OptiScaler/amd_fidelityfx_vk.dll` | 9,3 MB |
| `OptiScaler/D3D12_OptiScaler/D3D12Core.dll` | 3,4 MB |
| pozostałe | ~0,6 MB |

Razem około **182 MB runtime'u**, które lista dozwolonych katalogów
odrzucała po cichu. Instalacja zgłosiłaby sukces, a mod byłby niekompletny —
gorszy rodzaj błędu niż jawna odmowa.

Naprawa: zamiast listy dozwolonych katalogów obowiązuje lista wykluczeń —
skrypty instalacyjne (`.bat`, `.cmd`, `.sh`, `.ps1`) oraz plik znacznika bez
rozszerzenia. Reszta trafia do gry z zachowaniem ścieżki względnej, więc
zmiana układu paczki nie może już nic zgubić. Licencje pozostają, bo payload
jest na GPL.

### Wniosek

Oba błędy wynikały z tego samego wzorca: dopasowałem jedno miejsce do nowego
kanału, zakładając, że reszta potoku jest formatoagnostyczna. Nie była.
Żaden test tego nie łapał, bo wszystkie używały układu paczki oficjalnej.
Doszły trzy testy formatu archiwum oraz test instalacji i deinstalacji
w układzie forka, oparty na rzeczywistej zawartości wydania `v0.2.0-dlssnr`.

## Dowody walidacji

- Testy jednostkowe: `GameShift.UnitTests` 60/60 w filtrze OptiScaler,
  `GameShift.SecurityTests` 5/5 dla sondy DriverStore. Build 0 ostrzeżeń.
- Sonda uruchomiona na maszynie deweloperskiej poprawnie rozpoznaje
  Radeona jako sprzęt bez wsparcia i nie rzuca wyjątku.
- Założenia patchera potwierdzone na prawdziwym `OptiScaler.ini` z paczki
  forka: sekcje `[Upscalers]`, `[DLSS]`, `[DlssNr]` istnieją, a wartości
  wpisywane przez patcher są identyczne z ręcznie zweryfikowaną,
  działającą konfiguracją (`dlss`, `dlss_12`, `dlss`, `true`, `true`).
- Plik ma pięć wystąpień `Enabled=auto` w innych sekcjach, co potwierdza
  konieczność wiązania klucza z sekcją; przypadek jest pokryty testem.
- Po etapie 4: build całego rozwiązania 0 błędów i 0 ostrzeżeń; testy
  UnitTests 180/180, RecoveryTests 13/13, SecurityTests 9/9,
  IntegrationTests 183/186 (3 pominięte wymagają uprawnień administratora
  i były pomijane również przed tą zmianą).
- Testy integracyjne pokrywają: instalację z modelem ze sterownika,
  zachowanie BOM, deinstalację czyszczącą katalog gry, odrzucenie na sprzęcie
  innym niż GeForce, na starszej generacji, na zbyt starym sterowniku, przy
  braku modelu, przy paczce bez wymaganych kluczy INI, oraz to, że instalacja
  bez Neural Rendering w ogóle nie odpytuje sterownika i nie rusza INI.

## Ryzyka i decyzje otwarte

- Ścieżek DLSS nie da się zweryfikować na maszynie deweloperskiej z Radeonem.
- Nowe wydanie forka wymaga ręcznej zmiany przypiętego skrótu w kodzie.
- `nvngx_dlssnr.dll` waży ~158 MB. Kopiowanie go do każdego katalogu gry
  rozdmuchuje zużycie dysku — do rozważenia twarde dowiązanie zamiast kopii,
  o ile katalog gry leży na tym samym woluminie co DriverStore.

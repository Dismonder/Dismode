# Plan wykonawczy: moduł OptiScaler

Status: aktywny — moduł działa i jest wydany w 0.5.1. Ścieżka FSR/XeSS
zweryfikowana. Ścieżka DLSS Neural Rendering **nigdy nie została zobaczona
działająca w grze** — to jedyna otwarta brama.
Utworzono: 2026-09-05
Zaktualizowano: 2026-09-10 (wydanie 0.5.1)

## Cel

Wstrzykiwać OptiScaler do katalogu gry w sposób odwracalny: każdy nadpisany
plik ma kopię zapasową, każda instalacja ma manifest, a deinstalacja odmawia,
gdy ktoś zmienił pliki po drodze. Dla gier, które wymagają czegoś ponad
domyślne ustawienia, moduł zna te wymagania z góry i sam je spełnia.

## Zakres bieżący

Po ustaleniu z 2026-09-10 moduł koncentruje się na **OptiScalerze i dodatkach
potrzebnych do jego działania w konkretnych grach**. Wątek DLSS 5 / Neural
Rendering pozostaje w kodzie i jest kompletny, ale nie jest już motorem
rozwoju — czeka wyłącznie na potwierdzenie w grze (etap 5).

## Stan wyjściowy

| Element | Plik | Co robi |
|---|---|---|
| polityka bezpieczeństwa | `src/GameShift.Core/OptiScaler/OptiScalerSafetyPolicy.cs` | bramki gry uruchomionej, anti-cheatu i potwierdzenia; mapowanie proxy DLL |
| katalog wymagań gier | `src/GameShift.Core/OptiScaler/OptiScalerGameRequirements.cs` | proxy, ustawienia INI, wymagany dodatek i pliki kolidujące — per gra |
| patcher INI | `src/GameShift.Core/OptiScaler/OptiScalerIniPatcher.cs` | zamknięta lista kluczy, zachowanie BOM i CRLF |
| źródło paczek | `src/GameShift.Windows/OptiScaler/GitHubOptiScalerPackageSource.cs` | cztery kanały, allow-lista hostów, SHA-256, format archiwum zależny od kanału |
| źródło dodatków | `src/GameShift.Windows/OptiScaler/REFrameworkCompanionSource.cs` | przypięte wydanie REFramework, weryfikacja SHA-256 |
| bramka plików NGX | `src/GameShift.Windows/OptiScaler/NeuralRenderingModelImporter.cs` | podpis Authenticode + podmiot „NVIDIA Corporation" |
| sonda sterownika | `src/GameShift.Windows/OptiScaler/NvidiaDriverStoreProbe.cs` | wersja sterownika, obecność `nvngx_*` w DriverStore |
| manager | `src/GameShift.Windows/OptiScaler/OptiScalerManager.cs` | transakcyjny deploy, kopie zapasowe, manifest, wykrywanie obcych zmian, rollback |

## Granice bezpieczeństwa

- Instalator GameShift nie zawiera ani OptiScalera, ani DLL-ek NVIDII.
  OptiScaler jest na GPL-3.0, `nvngx_*` należą do NVIDII.
- Każde wydanie zewnętrzne jest **przypięte** tagiem i skrótem SHA-256 w kodzie.
  Nowsza wersja wymaga zmiany kodu i przejścia testów — nigdy nie instalujemy
  „najnowszego".
- Patch INI zmienia wyłącznie zamkniętą listę kluczy. Wartości pochodzą z enum,
  nigdy z tekstu użytkownika.
- Pliki NGX przechodzą przez weryfikację podpisu. Kopia, której Windows nie
  potwierdza, wymaga osobnej, świadomej zgody — i tylko dlatego, że **żadna
  publicznie krążąca kopia `nvngx_dlssnr.dll` tej weryfikacji nie przechodzi**.

| Plik | Źródło | Weryfikacja |
|---|---|---|
| paczka OptiScaler | GitHub Releases, przypięty tag | SHA-256 w kodzie |
| REFramework | GitHub Releases, przypięty nightly | SHA-256 w kodzie |
| `nvngx_dlssnr.dll` (~158 MB) | DriverStore, a gdy go tam nie ma — plik wskazany przez użytkownika | podpis NVIDIA; kopia bez podpisu tylko za osobną zgodą |
| `nvngx_dlss.dll` (~56 MB) | jak wyżej | jak wyżej |
| `nvngx_dlssd.dll`, `nvngx_dlssg.dll` | DriverStore | podpis NVIDIA |

## Warstwy i bramki sprzętowe

| Warstwa | Wymaganie | Bez wymagania |
|---|---|---|
| OptiScaler + FSR/XeSS | dowolne GPU DX12 | dostępna |
| DLSS Super Resolution | GeForce RTX 20+ | pozycja nieaktywna z powodem |
| DLSS Neural Rendering | GeForce RTX 50+, sterownik 616.56+, obie biblioteki NGX | pozycja nieaktywna z powodem |

Maszyna deweloperska ma Radeona RX 9070 XT. Ścieżek DLSS nie da się tu
zweryfikować end-to-end — potwierdzenie wymaga maszyny `ERYK` (RTX 5070).

## Etapy

### 1–4. Kanał, sonda, patch INI, wpięcie w UI

Status: ukończone (0.4.x)

Kanał `DlssNeuralRendering` z przypiętym wydaniem forka; `NvidiaDriverStoreProbe`
z przeliczeniem czteroczłonowej wersji Windows na numerację NVIDII; patch
zamkniętej listy kluczy zachowujący BOM i CRLF; bramka sprzętowa działa
**przed** pobraniem czegokolwiek, więc odmowa nie generuje ruchu sieciowego.

Świadomie pominięte: twarde dowiązanie zamiast kopii dla `nvngx_dlssnr.dll`.
Kopiowanie działa niezależnie od tego, czy katalog gry leży na tym samym
woluminie co DriverStore. Optymalizacja ma sens, gdy okaże się uciążliwa.

### 5. Weryfikacja Neural Rendering na maszynie z RTX 5070

Status: **zablokowany** — jedyna otwarta brama modułu

Co ustalono na `ERYK`:

| Data | Sterownik | Ustalenie |
|---|---|---|
| 2026-09-05 | `32.0.16.1088` → 610.88 | poniżej progu 616.56, moduł poprawnie odmawiał |
| 2026-09-06 | 616.64 | próg spełniony, ale pakiet sterownika zawiera wyłącznie `nvngx.dll`, `nvngx_dlssg.dll` i `nvngx_update.exe` |

Wniosek, który przewrócił założenie etapu 2: **sterownik nie niesie ani
`nvngx_dlssnr.dll`, ani `nvngx_dlss.dll`**. Pozyskanie z DriverStore, na którym
opierał się cały etap 2, dla tych dwóch nazw nie zadziała na żadnej znanej
wersji sterownika. Stąd ścieżka „plik wskazany przez użytkownika".

Stan plików, które użytkownik posiada (`Documents\GameShift-DLSS5`):

| Plik | Podpis Authenticode |
|---|---|
| `nvngx_dlss.dll` | **Valid**, CN=NVIDIA Corporation |
| `nvngx_dlssnr.dll` | **HashMismatch** — zmieniony po podpisaniu przez NVIDIĘ |

Ten drugi przechodzi wyłącznie przez świadomą zgodę. To nie jest formalność:
plik dostaje pełne prawa procesu gry, a Windows nie potwierdza, że to
niezmieniony kod NVIDII.

Próg 616.56 pochodzi z ustaleń sesji „DLL5 dla RTX5070". Próbowałem go
potwierdzić w materiałach NVIDII — **nie da się**: strony produktowe mówią
„zainstaluj najnowszy sterownik" i nie podają numeru wersji dla tej funkcji.

Ale to pytanie w dużej mierze przestało mieć znaczenie. Skoro sterownik i tak
nie niesie żadnej z dwóch bibliotek NGX, a moduł wymaga teraz obecności obu
i sprawdza ich podpis, prawdziwą bramką jest obecność plików, nie numer
sterownika. Próg został, bo tani i nie szkodzi, ale gdyby okazał się zły
w którąkolwiek stronę, skutek jest ograniczony: przy zbyt wysokim moduł
odmawia na sprzęcie, który by dał radę, przy zbyt niskim i tak zatrzyma go
brak bibliotek. Poprawka to jedna stała `MinimumDriverVersion`.

Runbook do wykonania na `ERYK`:

1. Zainstaluj GameShift 0.5.1. Skopiuj cały folder `GameShift-DLSS5` — obie
   biblioteki NGX są potrzebne.
2. Okno OptiScaler, kanał „DLSS 5 Neural Rendering — fork". Opis pod
   przełącznikiem musi pokazać `Rtx50` i wersję w formacie NVIDII (`616.64`),
   nie czteroczłonowy numer Windows.
3. Wskaż obie biblioteki. Przy `nvngx_dlssnr.dll` zaznacz zgodę. Przełącznik
   Neural Rendering musi się odblokować dopiero wtedy, gdy **obie** są gotowe.
4. Zainstaluj. W katalogu gry sprawdź obecność obu `nvngx_*`, proxy DLL i
   `dinput8.dll`. W `OptiScaler.ini`: `[DlssNr] Enabled=true`,
   `Dx12Upscaler=dlss`, a plik nadal zaczyna się od BOM (`Format-Hex -Count 3`
   → `EF BB BF`).
5. Uruchom grę, `Home`, sekcja „DLSS Neural Rendering". Pole musi być dostępne;
   jeśli nie — powód pokaże się pod checkboxem, szczegóły w `OptiScaler.log`.
6. Deinstalacja: katalog gry wraca do stanu sprzed zmiany.
7. Wykrywanie obcych zmian: zainstaluj ponownie, ręcznie zmień jeden plik,
   spróbuj usunąć. Moduł musi odmówić i wskazać zmieniony plik.

### 6. Dodatki wymagane przez konkretne gry

Status: ukończone w kodzie (0.5.1), **niepotwierdzone w grze**

Katalog `OptiScalerGameRequirements` opisuje per gra: proxy DLL, ustawienia INI,
wymagany dodatek i pliki, które kolidują. Dla silnika RE:

- REFramework (praydog, MIT) instaluje się automatycznie jako `dinput8.dll`,
  z przypiętego wydania nightly, weryfikowany SHA-256;
- OptiScaler wchodzi jako `dxgi.dll` — to **metoda 1 z wiki OptiScalera** dla
  Resident Evil 9 Requiem;
- `Spoofing.Dxgi=false`, bo silnik RE się przy spoofingu wywala;
- overlay OptiScalera przeniesiony z `Insert` na `Home` (`0x24`), bo REFramework
  też domyślnie siedzi na `Insert`, a dwa overlaye na jednym klawiszu nie
  działają. Wiki proponuje przestawić REFramework na `Delete`; przestawiamy
  OptiScalera, żeby nie kazać użytkownikowi konfigurować cudzego moda.

Gry objęte: `re9.exe`, `MonsterHunterWilds.exe` (REFramework), `Forspoken.exe`
(`d3d12.dll`), `D2R.exe` (`winmm.dll`), `DOA6.exe` (`d3d12.dll`).

## Defekty wykryte i naprawione

### 0.4.8 — kontrola formatu archiwum zaszyta na 7z

`GitHubOptiScalerPackageSource` sprawdzał bajty magiczne `37 7A BC AF 27 1C`
niezależnie od kanału. Poprawiono wtedy wyłącznie filtr **nazwy** assetu, nie
kontrolę **zawartości**. Fork publikuje ZIP, więc poprawna paczka była
odrzucana jako uszkodzona.

Pouczająca była kolejność kontroli: SHA-256 sprawdzane jest **przed** formatem
i przeszło. Przypięty skrót był prawidłowy — zawiódł wyłącznie błędny warunek.

### 0.4.8 — filtr payloadu gubił cały runtime forka

`BuildPayload` przepuszczał `.dll`/`.ini` z katalogu głównego oraz katalogi
`D3D12_Optiscaler\` i `Licenses\`. Fork trzyma wszystkie backendy w
`OptiScaler\` — około **182 MB runtime'u**, które lista dozwolonych katalogów
odrzucała po cichu. Instalacja zgłaszałaby sukces, a mod byłby niekompletny.
To gorszy rodzaj błędu niż jawna odmowa.

Naprawa: zamiast listy dozwolonych katalogów obowiązuje lista wykluczeń —
skrypty instalacyjne i plik znacznika bez rozszerzenia. Reszta trafia do gry
z zachowaniem ścieżki względnej.

Oba błędy miały ten sam wzorzec: dopasowałem jedno miejsce do nowego kanału,
zakładając, że reszta potoku jest formatoagnostyczna. Nie była.

### 0.5.1 — zły układ plików dla silnika RE

0.5.0 instalowała REFramework jako `ReShade64.dll` i kazała OptiScalerowi
ładować go samemu przez `Plugins.LoadReshade=true`. To było moje obejście
konfliktu hooków, którego przy kolejności z metody 1 nie ma. Wiki OptiScalera
dla Requiem zaleca zostawić REFramework pod jego własną nazwą.

Naprawa: `CompanionFileName` wraca na `dinput8.dll`, `LoadReshade` znika,
a pliki odsunięte na bok przez 0.5.0 wracają na miejsce **przed** położeniem
nowego payloadu — dzięki temu obejmuje je zwykła kopia zapasowa i deinstalacja
oddaje użytkownikowi jego oryginał, zamiast zgubić go razem ze starym
manifestem.

### 0.5.1 — Neural Rendering pytał o jedną bibliotekę zamiast dwóch

Model `nvngx_dlssnr.dll` jeździ na runtimie `nvngx_dlss.dll`. Ustawienie
`Dx12Upscaler=dlss` bez tego drugiego zostawia grę z ustawieniem i niczym za
nim. Ponieważ sterownik 616.x nie niesie żadnej z nich, obie muszą móc
pochodzić od użytkownika.

Naprawa: `NeuralRenderingModelPaths` zamiast pojedynczej ścieżki, osobny
przycisk i osobny status dla każdej nazwy, wspólna bramka podpisu. Kopia
z DriverStore ma pierwszeństwo — tam pisze tylko TrustedInstaller.

## Dowody walidacji

- Pełna regresja 2026-09-10 na Win11: **458 testów zielonych**, 0 czerwonych,
  4 pominięte (wymagają uprawnień administratora albo realnego obciążenia).
  Build Release: 0 błędów, 0 ostrzeżeń.
- Testy integracyjne pokrywają: instalację z modelem ze sterownika, zachowanie
  BOM, deinstalację czyszczącą katalog gry, odrzucenie na sprzęcie innym niż
  GeForce, na starszej generacji, na zbyt starym sterowniku, przy braku modelu,
  przy paczce bez wymaganych kluczy INI, instalację w układzie forka oraz to,
  że instalacja bez Neural Rendering w ogóle nie odpytuje sterownika.
- Sonda na maszynie deweloperskiej poprawnie rozpoznaje Radeona jako sprzęt
  bez wsparcia i nie rzuca wyjątku.
- Wartości wpisywane przez patcher potwierdzone na prawdziwym `OptiScaler.ini`
  z paczki forka. Plik ma pięć wystąpień `Enabled=auto` w innych sekcjach, co
  potwierdza konieczność wiązania klucza z sekcją — przypadek pokryty testem.

## Etapy naprawy — kolejność

Uporządkowane od najtańszego dowodu do najdroższego. Każdy etap kończy się
faktem, nie opinią.

| # | Co | Dlaczego teraz | Kto może to zrobić |
|---|---|---|---|
| N1 | Uruchomić RE9 z 0.5.1 i sprawdzić, czy OptiScaler w ogóle wstaje | Metoda 1 jest wzięta z wiki, nie z własnego testu. Dopóki tego nie widzieliśmy, cały etap 6 jest niepotwierdzony | użytkownik na `ERYK` |
| N2 | Przejść runbook etapu 5, kroki 4–5 | To jedyna brama, która blokuje zamknięcie modułu | użytkownik na `ERYK` |
| N3 | Sprawdzić, czy `nvngx_dlssnr.dll` z HashMismatch faktycznie działa, czy tylko się ładuje | Jeśli nie działa, cała ścieżka Neural Rendering jest martwa i lepiej to wiedzieć, niż utrzymywać | użytkownik na `ERYK` |
| ~~N4~~ | ~~Zweryfikować próg `MinimumDriverVersion`~~ | **Sprawdzone 2026-09-10, bez rozstrzygnięcia.** Materiały NVIDII nie podają numeru wersji dla tej funkcji. Wniosek niżej | — |
| ~~N5~~ | ~~Test deinstalacji po aktualizacji 0.5.0 → 0.5.1~~ | **Zrobione 2026-09-10.** Mój wcześniejszy wpis twierdził, że istnieje test jednostkowy — nie istniał żaden. Szczegóły niżej | — |
| N6 | Rozszerzyć katalog wymagań o kolejne gry z listy zgodności | Dopiero po N1 — nie ma sensu mnożyć wpisów w formacie, którego nie potwierdziliśmy | ja |

## Ścieżka aktualizacji 0.5.0 → 0.5.1 — domknięta 2026-09-10

Sprawdzone niezależnie przez Codeksa, żeby wynik nie był tylko moim zdaniem.
Zgodziliśmy się co do dwóch punktów, nie zgodziliśmy co do dwóch.

| Pytanie | Werdykt |
|---|---|
| Czy kopia zapasowa obejmuje oryginał gracza, czy już podmieniony plik? | oryginał — przywrócenie biegnie **przed** utworzeniem kopii |
| Czy wpis o odsunięciu zostaje w manifeście i deinstalacja przywróci go drugi raz? | nie zostaje, manifest budowany jest od nowa |
| Czy migawka rollbacku obejmowała przywracany plik? | **nie obejmowała wprost** — wychodziło to tylko z tego, że ta sama nazwa była w payloadzie |
| Czy awaria między przywróceniem a zapisem manifestu uruchamia rollback? | **nie uruchamiała** — obie pętle stały poza `try` |

Dwa ostatnie nie prowadziły do utraty pliku, bo operacje są idempotentne,
a stary manifest pozostawał ważny. Ale poprawność wychodziła z przypadku,
nie z konstrukcji. Poprawione: ścieżki przywracanych plików wchodzą do migawki
jawnie, a obie pętle biegną wewnątrz transakcji.

Test: `UpgradeOutOfTheDisplacingLayoutGivesTheOriginalFileBack`. Zapisuje stan,
który zostawiła 0.5.0 — surowy JSON manifestu plus kopia odsuniętego pliku —
robi aktualizację, deinstaluje i sprawdza, że w katalogu gry leży oryginał
gracza. Sprawdziłem, że test ma zęby: po wyłączeniu pętli przywracania pada.

## Ryzyka i decyzje otwarte

- **Największe:** ścieżka Neural Rendering nie została nigdy zobaczona
  działająca. Wszystko poniżej UI jest przetestowane jednostkowo, ale to nie
  to samo co obraz na ekranie.
- Wiki ostrzega, że REFramework rozbija się o antymodową ochronę Capcomu po
  każdej aktualizacji gry. Świeża aktualizacja RE9 może oznaczać, że zgodnego
  REFramework po prostu jeszcze nie ma — i nie jest to defekt GameShifta.
- Nowe wydanie forka i nowy nightly REFramework wymagają ręcznej zmiany
  przypiętego skrótu w kodzie. To celowe.
- `nvngx_dlssnr.dll` waży ~158 MB i jest kopiowany do każdego katalogu gry.
  Do rozważenia twarde dowiązanie, o ile katalog gry leży na tym samym
  woluminie co źródło.

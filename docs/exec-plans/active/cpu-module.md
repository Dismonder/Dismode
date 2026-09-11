# Plan wykonawczy: moduł CPU

Status: podstawa zbudowana i zmierzona, rozbudowa w toku
Utworzono: 2026-09-06
Ostatnia aktualizacja: 2026-09-06

## Cel

Doprowadzić moduł CPU do stanu, w którym zastępuje Process Lasso w zastosowaniu
growym — mierzalnie, nie deklaratywnie. Miarą jest ogon opóźnień wybudzenia pod
obciążeniem, a docelowo czasy klatek z PresentMon, nie średnie zużycie procesora.

## Stan wyjściowy

Zbudowane i sprawdzone na maszynie:

- odczyt topologii przez `GetSystemCpuSetInformation`, weryfikowany rozmiarem
  bufora i liczbą wpisów;
- polityka przypinania — odmawia domyślnie, przypina tylko na procesorze
  o rdzeniach różnych klas;
- silnik ProBalance z histerezą, minimalnym przytrzymaniem, maksymalnym czasem
  ograniczenia, karencją i limitem jednoczesnych ograniczeń;
- nadzorca z aktuatorem zapisującym do journala;
- akcja affinity: nałożenie, weryfikacja, odtworzenie z journala;
- przełącznik w ustawieniach dociągnięty przez gRPC do sesji;
- przypinanie świadome cache ostatniego poziomu — gra zostaje w jednej
  grupie cache na procesorze o jednorodnych rdzeniach, gdzie wcześniej
  polityka odmawiała całkowicie;
- domyślne zbiory procesorów jako mechanizm podstawowy, twarda maska
  awaryjnie;
- odsuwanie ograniczonych procesów tła na rdzenie poza grą.

Zmierzone na i7-11700F, 16 obciążaczy na 16 wątkach, cztery przebiegi:
p99 opóźnienia wybudzenia spadło o 1,2–7,7 ms (średnio ~5 ms), mediana
z ~17,1 do ~15,5 ms. Poprawa w każdym przebiegu.

Pełne wyniki i metoda: `C:\Users\Damia\Documents\GameShift-DLSS5\porownanie-cpu.md`.

## Czego nie wiemy

Te punkty są warunkami wstępnymi, nie zadaniami do odhaczenia — dopóki nie
zostaną domknięte, każda liczba w tym planie jest wstępna.

1. **Ścieżka hybrydowa nie została wykonana ani razu.** Maszyna deweloperska ma
   jednorodny i7-11700F, więc polityka na niej zawsze odmawia. Cała część
   przypinania do rdzeni wydajnych opiera się na testach z odtworzoną
   topologią Arrow Lake.
2. **Nigdy nie uruchomiono tego z prawdziwą grą.** Obciążenie w pomiarach to
   procesy palące rdzeń w pętli. Gra ma inny profil: wątek renderujący,
   strumieniowanie zasobów, sterownik graficzny.
3. **Progi są wymyślone.** 0,75 rdzenia, 3 próbki, 5 s przytrzymania, 30 s
   karencji — dobrane zachowawczo, nie dostrojone pomiarem.
4. **Wpięcie w orkiestrator nie ma testu automatycznego.** Test na poziomie
   sesji okazał się niestabilny i został usunięty zamiast zostawiania go
   w zestawie.

## Etapy

### 1. Domknięcie tego, co zbudowane

1. Wykonać pomiar na procesorze hybrydowym: potwierdzić, że topologia raportuje
   dwie klasy wydajności, że maska gry nie obejmuje żadnego rdzenia
   energooszczędnego i że przypięcie wraca po sesji.
2. Zmierzyć wpływ samego przypinania osobno od ograniczania tła — to dwie różne
   dźwignie i mieszanie ich zaciera, która działa.
3. Odtworzyć test wpięcia w sesję w postaci, która nie zależy od procesu GUI ani
   od prawdziwego obciążenia.

### 2. Dostrojenie progów pomiarem

4. Rozszerzyć aparaturę o odczyt z PresentMon, żeby miarą był czas klatki gry,
   a nie zastępczy pomiar opóźnienia wybudzenia.
5. Przemieść progi po siatce: próg złapania, liczba próbek, minimalne
   przytrzymanie. Zapisać wyniki tak, żeby dało się je powtórzyć.
6. Ustalić progi domyślne na podstawie tych wyników i zapisać w kodzie, skąd
   pochodzą.

### 3. Mocniejsze narzędzia ograniczania

Obniżenie priorytetu to najsłabsza z dostępnych dźwigni. Windows daje mocniejsze
i GameShift już ich używa gdzie indziej.

7. Ograniczanie stopniowane: priorytet → ECO QoS → mocniejsze odsunięcie.
   Dziś priorytet i odsunięcie nakładają się razem, przy pierwszym złapaniu.
   Eskalacja ma dawać słabszy środek najpierw i sięgać po mocniejszy dopiero,
   gdy przez kilka próbek nie pomógł — proces jednorazowo ożywiony nie
   powinien od razu dostawać wszystkiego.
8. ~~Odsuwanie tła od rdzeni gry~~ — zrobione przez domyślne zbiory
   procesorów, sprawdzone na żywym procesie.
9. Priorytet pamięci dla procesów ograniczonych — `SetProcessInformation`
   z `ProcessMemoryPriority`. Proces czytający z dysku potrafi psuć płynność,
   nie zjadając procesora. Priorytet wejścia-wyjścia jest osiągalny tylko
   przez nieudokumentowane `NtSetInformationProcess`, więc wchodzi wyłącznie
   wtedy, gdy pomiar pokaże, że sam priorytet pamięci nie wystarcza.
10. Przypisanie per wątek zamiast per proces. Gra ma jeden wątek renderujący,
    który liczy się bardziej niż reszta jej wątków, a
    `SetThreadSelectedCpuSets` pozwala go wyróżnić. Wymaga rozpoznania, który
    wątek to jest — samo zgadywanie po zużyciu procesora jest zawodne.

### 4. Koszt własny pętli i zasilanie

Nadzorca ma pilnować płynności, więc sam nie może jej psuć. Zmierzone: pełne
przejście mieści się w interwale z zapasem, ale to pomiar na bezczynnej maszynie
i przy pustym aktuatorze.

11. Zmienny interwał: rzadziej, gdy maszyna jest spokojna, gęściej pod
    obciążeniem. Stała częstotliwość płaci pełny koszt wtedy, gdy nic się nie
    dzieje.
12. Ograniczyć inwentaryzację do procesów, które mogą być kandydatami —
    pełne wyliczenie wszystkich procesów co dwie sekundy jest pracą wykonywaną
    w trakcie gry.
13. Dodać do pomiaru wariant z aktywnym aktuatorem, żeby koszt zapisu do
    journala i kontroli podpisu był policzony, a nie założony.
14. Ograniczyć zużycie zasobów przez sam GameShift w trakcie sesji. Narzędzie
    pilnujące płynności, które samo zjada rdzeń, jest gorsze niż jego brak.
15. Bramka na przełączanie planu zasilania, żeby krótka zmiana obciążenia nie
    powodowała migotania między planami.
16. Żądania zasilania na czas sesji, blokujące dławienie i usypianie —
    `PowerSetRequest` z `PowerRequestExecutionRequired`.

### 5. Trwałość i zakres

17. Zapamiętywanie ustawień CPU per gra w istniejących profilach optymalizacji,
    zamiast jednego przełącznika globalnego.
18. Pokazywanie w interfejsie, co zostało ograniczone i dlaczego — silnik zna
    powód każdej decyzji, a obecnie nikt go nie widzi.
19. Obsługa więcej niż jednej grupy procesorów. Dziś polityka odmawia powyżej
    64 procesorów logicznych; domyślne zbiory nie mają tego ograniczenia, więc
    droga jest otwarta. Poza sprzętem, o który tu chodzi, ale to jedyne
    miejsce, gdzie moduł odmawia z powodu własnego ograniczenia, a nie
    dlatego, że nie ma czego poprawiać.

## Skąd wzięły się niektóre rozwiązania

Dwie rzeczy w tym module pochodzą z lektury ThreadPilota
(`PrimeBuild-pc/ThreadPilot`, AGPL-3.0). Kod jest własny; stamtąd pochodzi
rozpoznanie problemu.

Pierwsza: rdzenie tej samej klasy wydajności potrafią być podzielone cache'em
ostatniego poziomu. Polityka widziała wyłącznie klasę wydajności i na Ryzenie
z kilkoma CCD odmawiała, choć właśnie tam jest duży zysk.

Druga, ważniejsza: maska affinity jest regułą, a domyślne zbiory procesorów są
podpowiedzią. Maska zbyt wąska zagłodzi grę, zbiory nie. Do tego pułapka —
proces z już zawężonym affinity ignoruje zbiory całkowicie, a odczyt zwrotny
mimo to zwraca zapisane wartości, więc sama weryfikacja przez odczyt zgłosiłaby
sukces przypisania, które nie ma prawa zadziałać.

## Inwarianty

- Powłoka, stos wejścia, kompozytor, anti-cheat i drzewo gry pozostają
  nietknięte niezależnie od stopnia eskalacji.
- Maskę wolno wyłącznie zwężać w granicach tego, co proces już ma przydzielone.
- Każda zmiana przechodzi przez journal i wraca po sesji albo po awarii.
- Ograniczanie nie włącza się bez zgody użytkownika.
- Zmiana progów nie może być uzasadniona wyłącznie pomiarem opóźnienia
  wybudzenia — wiążący jest czas klatki gry.
- Nie kopiujemy algorytmu Process Lasso. Progi mają wynikać z pomiaru na
  konkretnym sprzęcie, a nie z odtwarzania cudzych stałych.
- Sterowanie procesami tła pozostaje podpowiedzią, nie regułą. Twarda maska
  na cudzym procesie mogłaby go zatrzymać, a nikt nas nie prosił o ruszanie
  go w ogóle.

## Walidacja

- na procesorze hybrydowym maska gry i maska tła są rozłączne, a obie wracają
  po sesji;
- eskalacja zatrzymuje się na najsłabszym stopniu, który wystarcza;
- p99 czasu klatki w grze nie pogarsza się przy żadnym stopniu eskalacji;
- pełne przejście pętli z aktywnym aktuatorem mieści się w interwale;
- po awarii procesu odtwarzanie z journala przywraca priorytety i maski;
- zestaw testów nie zawiera testu zależnego od procesu GUI ani od prawdziwego
  obciążenia maszyny budującej.

## Poza zakresem

Świadomie nie odtwarzamy tych funkcji Process Lasso:

- **Disallowed Processes** — automatyczne ubijanie procesów;
- **Keep Running** — automatyczne wznawianie procesów;
- **reguły trwałe poza sesją** — GameShift cofa wszystko po grze i to jest jego
  model, nie brak;
- **Instance Balancer**, **Group Extender** — istotne przy wielu gniazdach
  i powyżej 64 procesorów logicznych, czyli poza sprzętem, o który tu chodzi.

## Pomiar na grze ograniczonej procesorem — 2026-09-10

Pierwsza odpowiedź na najważniejsze pytanie tego modułu. **Jest przecząca.**

Gra: 7 Days To Die, postać stojąca AFK w jednym miejscu, bez interfejsu.
Obciążenie: tyle pętli PowerShella, ile maszyna ma rdzeni. Pomiar parowany
z przeplotem, cztery rundy, bloki po osiem sekund, p99 jako miara przycięć.

| Przebieg | p99 bez ograniczania | p99 z ograniczaniem | Różnica | Rund z poprawą |
|---|---|---|---|---|
| 1 | 17,55 ms | 17,57 ms | +0,02 ms | 1 z 4 |
| 2 | 17,81 ms | 17,62 ms | −0,19 ms | 3 z 4 |

**Przebiegi przeczą sobie co do kierunku.** To jest tu najmocniejszy dowód:
gdyby efekt istniał, oba pokazałyby to samo. Różnice rzędu 0,2 ms na 17,7 ms
to jeden procent, a rozrzut między rundami wewnątrz jednego wariantu bywa
większy. Pokazanie samego drugiego przebiegu jako „3 z 4 rund lepsze" byłoby
prawdą i byłoby wprowadzeniem w błąd.

### Wynik zerowy, nie pomiar pustki

„Brak poprawy" i „nic nie zostało ograniczone" wyglądają w liczbach
identycznie. Dlatego test raportuje teraz decyzje: **30 i 32 decyzje
ograniczenia, wszystkie na `powershell`**, ani jedna na proces gry. Moduł
trafił dokładnie w cel. Osobno potwierdzone, że priorytet naprawdę się zmienia
i wraca — `LiveProBalanceTests` na prawdziwym procesie liczącym.

### Co obciążenie faktycznie robi

Bez obciążenia ta sama gra: **9,88 ms na klatkę (101 FPS)**. Pod obciążeniem
p99 stoi na 17,5 ms w każdym pomiarze, w obu wariantach. Obciążenie kosztuje
ponad siedem milisekund i moduł **nie odzyskuje z tego nic**.

Prawdopodobne wyjaśnienie: obniżenie priorytetu nie wystarcza, gdy procesów
liczących jest tyle, ile rdzeni — nadal dostają czas. Procesor tej maszyny jest
jednorodny, więc odsuwanie tła na inne rdzenie nie miało dokąd.

### Czego ten pomiar nie rozstrzyga

- Obciążenie jest syntetyczne. Prawdziwe tło — przeglądarka, Discord,
  kompilacja — ma inny profil: budzi się falami, nie liczy bez przerwy.
- Maszyna jest jednorodna. Ścieżka hybrydowa P/E i wielo-CCD nadal nigdy się
  nie wykonała.
- Nie testowano drabiny eskalacji, bo jej nie ma — dziś moduł umie tylko
  obniżyć priorytet i odsunąć od rdzeni gry.

### Konsekwencja dla planu

Teza „ten moduł zastąpi Process Lasso" **nie ma na razie żadnego poparcia
w pomiarze**. Zanim dołoży się do niego cokolwiek, trzeba odpowiedzieć, czy
scenariusz, w którym miałby pomagać, w ogóle istnieje na tej maszynie —
a jeśli tak, to dlaczego samo obniżenie priorytetu go nie łapie.

## Przebudowa na twarde maski — 2026-09-10

Wynik zerowy z pomiaru wyżej postawił pytanie: czy zły jest pomysł, czy
mechanizm. Test trzech warunków na przemian odpowiedział jednoznacznie.

| Warunek | p99 |
|---|---|
| tło wolne | 16,80 ms |
| tło z obniżonym priorytetem — **to robił moduł** | 16,36 ms |
| tło przypięte na twardo do 4 z 16 wątków | **10,65 ms** |

**−6,15 ms, 36,6%. Maska wygrała z priorytetem w 4 rundach na 4.** Bez
obciążenia ta sama gra chodziła 9,88 ms — maska odzyskuje niemal całą stratę,
priorytet nie odzyskiwał nic.

Wyjaśnienie: priorytet i CPU Sets to dla planisty Windows **wskazówki**, które
przy tylu wątkach liczących, ile ma maszyna, zostają przegłosowane. Maska
powinowactwa to reguła, której planista złamać nie może.

### Co zmieniono

- `CpuAffinityPolicy` odmawiał na maszynach jednorodnych z uzasadnieniem
  „wszystkie rdzenie tej samej klasy, przypinanie nic nie zmieni". Dla **gry**
  to prawda — lokalność cache'u jest wtedy bez znaczenia. Dla **tła** fałsz,
  bo sedno nie jest w lokalności, tylko w odebraniu rdzeni. Rola `Background`
  dostaje teraz ćwiartkę maszyny, o ile ma co najmniej osiem wątków
  logicznych.
- `JournaledProBalanceActuator` nakłada tę maskę przez `ProcessAffinityAction`
  — z zapisem do dziennika, bo maska przeżywa śmierć GameShifta i musi
  zostać co odwrócić. Miękkie CPU Sets zostają obok jako dodatek.

### Pułapka pomiarowa, którą trzeba było zamknąć

Trzy przebiegi wróciły z samymi zerami i **przeszły jako wynik**. Przyczyna:
gra zostawiona bez fokusu dalej liczy świat — zmierzone trzy rdzenie zajęte —
ale przestaje wystawiać klatki, więc PresentMon nie ma czego mierzyć. Zero
klatek i „ograniczanie nic nie dało" wyglądają w liczbach identycznie.

Oba testy mają teraz kontrolę wstępną: krótkie próbne przechwycenie i
`Inconclusive` z konkretnym powodem, gdy gra nie rysuje. Do tego twarde
sprawdzenie, że żaden blok nie zwrócił zera.

Drugi problem: pomiar zostawia po sobie sesję ETW `gameshift-frametime` —
także po przebiegu zakończonym normalnie — a `--stop_existing_session` jej nie
sprząta. Sesja blokuje potem przechwytywanie **wszystkim** na maszynie.
Sprząta ją `EtwSessionCleanup`, wołany też z testu przed pomiarem.

### Czego nadal nie wiemy

Wynik 36,6% pochodzi z jednego przebiegu na jednej maszynie i jednej grze,
przy syntetycznym obciążeniu. Powtórka czeka na moment, gdy gra będzie na
pierwszym planie.

## Czy ten moduł w ogóle się odpala — 2026-09-10

Mechanizm działa i daje 36%. Osobne pytanie brzmi, czy warunki, w których
się uruchamia, kiedykolwiek zachodzą u użytkownika.

Zmierzone przez 20 sekund na maszynie deweloperskiej, w normalnym stanie
z uruchomioną grą:

| Proces | Rdzenie |
|---|---|
| 7DaysToDie | 3,38 |
| RadeonSoftware | 0,07 |
| wszystko inne | poniżej progu pomiaru |

Obciążenie systemu: **22,2%**. Próg `SystemLoadPercent` to 70. Jedyny proces
powyżej `RestrainAboveCores` = 0,75 to sama gra, której moduł nie dotyka.

**Wniosek: w codziennym użyciu ProBalance nigdy się nie uruchomi.** Blokują go
obie bramki naraz. Zmierzony zysk jest prawdziwy, ale występuje wyłącznie przy
obciążeniu, jakiego użytkownik zwykle nie ma.

### Czego to nie znaczy

Nie znaczy, że progi są za wysokie. Moduł jest reaktywny z założenia i ma
milczeć, gdy nic się nie dzieje — kompilacja, aktualizacja czy skan antywirusa
w trakcie grania to właśnie te rzadkie momenty, dla których powstał. Rzadkie
odpalanie może być poprawnym zachowaniem, nie usterką.

Nie znaczy też, że rozwiązaniem jest zakładanie maski od razu na starcie sesji.
Procesy, które nic nie liczą, nie odbierają grze niczego — zamknięcie ich
w ćwiartce nic by nie dało, a mogłoby zamulić pulpit.

### Eksperyment, który to rozstrzygnie

Pomiar przy **umiarkowanym** obciążeniu — cztery procesy liczące zamiast
szesnastu, czyli mniej więcej tyle, ile robi przeglądarka z rozmową wideo albo
kompilacja w tle. Trzy pytania naraz:

1. Czy czasy klatek w ogóle się psują przy takim obciążeniu?
2. Czy maska je ratuje, tak jak przy pełnym?
3. Czy obecne progi by się przy tym odpaliły?

Jeśli odpowiedzi wyjdą tak, tak, nie — to bramka obciążenia systemu jest
ustawiona źle i mamy na to dowód. Jeśli pierwsza wyjdzie nie, moduł jest
poprawny takim, jaki jest, i rzecz sprowadza się do rzadkich przypadków.

Wymaga gry na pierwszym planie.

## Bramka przebudowana na obciążenie tła — 2026-09-10

Mechanizm dawał 26%, ale test z **domyślnymi** ustawieniami pokazał zero
decyzji: bramki nie przepuszczały go nawet przy czterech procesach liczących
bez przerwy. Cały zysk był nieosiągalny dla użytkownika.

Bramka mierzyła obciążenie **całej maszyny** w procentach i było to złe
pytanie na dwa sposoby. Wliczała pracę samej gry jako dowód kontencji — a to
właśnie pracę gry chronimy, nie zakłócenie. Przez to zachowanie modułu zależało
od tego, jak ciężka jest gra: lekka gra plus cztery zajęte procesy tła dawały
31% i były ignorowane, te same cztery procesy obok ciężkiej gry dawały 41,7%
i były ograniczane, choć kradły dokładnie tyle samo rdzeni. Do tego procent
maszyny znaczy co innego na każdej maszynie — błąd, którego ten moduł unika
wszędzie indziej.

Teraz liczy się **wyłącznie tło, w rdzeniach**, sumowane z obserwacji, które
nadzorca i tak zbiera — bez osobnego próbkowania. Próg równy progowi
pojedynczego procesu (0,75). Pierwsza wartość, 1,5, wyglądała rozsądnie i była
niespójna: samotny proces powyżej progu pojedynczego nigdy by nie przeszedł,
bo suma tła jest co najmniej równa jego własnemu zużyciu.

Podstawa liczb: cicha maszyna z grą — najbardziej zajęty proces tła 0,07
rdzenia; cztery procesy liczące — 4,04 rdzenia i zmierzony zysk 10,6%.
Dziesięciokrotny odstęp między tymi przypadkami.

Potwierdzenie: `ShippedThresholdsActUnderModerateContention` — jedyny test,
który nie podmienia żadnego progu. Przed zmianą zero decyzji, po zmianie trzy.
Zniknął też martwy parametr `systemLoad` nadzorcy, który po przebudowie nic
już nie robił, a testy nadal go podawały, jakby sterował zachowaniem.

## Funkcja była martwa i testy tego nie łapały — 2026-09-10

Instalatory 0.6.0, 0.6.1 i 0.6.2 zawierały twardą maskę, która **nigdy by się
nie nałożyła** na jednorodnym procesorze. Poprawka `CpuAffinityPolicy`,
otwierająca ścieżkę dla roli `Background`, przepadła między sesjami: commit
`f44a8e6` przeniósł sam stały próg, a metodę `DecideBackgroundCorner` zgubił.
Gałąź jednorodna nadal odmawiała obu rolom, więc `ResolveBackgroundAffinityMask`
zwracał zero i aktuator nie miał czego nakładać.

Dlaczego nikt tego nie zauważył: istniejący test `UniformCpuIsLeftAlone`
wymagał odmowy dla **obu** ról i przechodził — czyli zielony zestaw testów
aktywnie potwierdzał zepsuty stan. Zmieniłem zachowanie polityki i nie
dopisałem wtedy ani jednego testu jednostkowego, więc jedyne, co o niej
mówiło, było napisane pod stare założenie.

Naprawione i pokryte:

- `UniformCpuLeavesTheGameAlone` — gry nadal nie przypinamy, bo przy
  identycznych rdzeniach i wspólnym cache'u to nic nie zmienia;
- `UniformCpuStillConfinesBackground` — tło dostaje ćwiartkę, maska obejmuje
  najniższe procesory logiczne;
- `SmallUniformCpuIsLeftAloneEvenForBackground` — poniżej ośmiu wątków ćwiartka
  jest za grubym cięciem i polityka odmawia;
- `RealTopologyProducesABackgroundMask` — **na prawdziwym sprzęcie**, nie na
  fikstruze. Na maszynie deweloperskiej: 16 wątków, maska `0xF`.

Wniosek na przyszłość: zmiana zachowania bez własnego testu jest nie tylko
niepokryta — potrafi być aktywnie maskowana przez test napisany pod poprzednie
założenie.

## Pomiar na żywej rozgrywce — Valheim, 2026-09-10

Najmocniejszy dotąd wynik i pierwszy zebrany **w trakcie normalnej gry**,
przy graczu swobodnie poruszającym się po świecie — nie na postaci stojącej
w miejscu. Inna gra niż poprzednio, więc i niezależne potwierdzenie.

Linia odniesienia bez obciążenia: 118,4 FPS, p99 **12,53 ms**.

### Pełne obciążenie — 16 pętli liczących

| Runda | tło wolne | priorytet | maska |
|---|---|---|---|
| 1 | 36,50 | 46,48 | **14,04** |
| 2 | 22,44 | 27,04 | **17,28** |
| 3 | 22,45 | 20,52 | **14,34** |
| 4 | 90,06 | 29,93 | **12,97** |
| mediana | 36,50 | 29,93 | **14,34** |

**−22,16 ms, 60,7%. Maska wygrała z priorytetem w 4 rundach na 4.** Z maską
gra wraca praktycznie do stanu bez obciążenia (14,34 wobec 12,53 ms).

### Umiarkowane obciążenie — 4 pętle

Mediana: wolne 15,16 ms, priorytet 18,49 ms, maska **14,07 ms** — zysk 7,2%,
maska lepsza w 3 rundach na 4.

### Obniżanie priorytetu bywa szkodliwe

Najważniejsze ustalenie tego przebiegu i takie, którego nie zakładałem.
Kolumna priorytetu **przebija kolumnę tła wolnego** w kilku rundach: 46,48
wobec 36,50 przy pełnym obciążeniu, 18,49 wobec 15,16 przy umiarkowanym.

Stary mechanizm nie był więc tylko bezużyteczny — bywał gorszy niż nierobienie
niczego. Wygląda to na inwersję priorytetów: zepchnięty proces trzymający
blokadę oddaje ją później, a gra na nią czeka. To wzmacnia decyzję
o przebudowie: maska nie zmienia kolejności dostępu do blokad, tylko odbiera
rdzenie.

### Bramka w prawdziwych warunkach

Tło zajmowało 15,42 rdzenia przy pełnym obciążeniu i 8,95 przy umiarkowanym,
przy progu 0,75 — bramka przepuszczała w obu przypadkach, zgodnie z zamiarem.
Zmierzone osobno przy zwykłej pracy gracza: tło 1,11 rdzenia sumarycznie, ale
najcięższy pojedynczy proces 0,18 — poniżej progu pojedynczego procesu, więc
moduł słusznie milczy. Cisza, gdy nie ma czego naprawiać.

## Pomiar z widocznym GPU, Sons of the Forest, 11 września 2026

Sekcja dopisana przez sesję pracującą nad aparatem pomiarowym i priorytetem
wejścia-wyjścia. Wcześniejsze pomiary w tym dokumencie powstały narzędziem,
które **nie widziało GPU** — PresentMon szedł z `--no_track_gpu` i
`--no_track_display`, więc wszystko poniżej jest pierwszym spojrzeniem na
drugą stronę klatki.

### Profil bazowy, 2339 klatek w 30 sekundach

| metryka | p50 | p99 | max |
|---|---|---|---|
| FrameTime | 12,45 ms | 17,38 ms | 375,65 ms |
| CPUBusy | 11,89 ms | 16,85 ms | — |
| GPUBusy | 6,70 ms | 7,14 ms | — |
| GPUWait | 5,71 ms | 10,30 ms | — |
| DisplayLatency | 23,43 ms | 31,36 ms | — |

`PresentMode` to `Hardware Composed: Independent Flip` dla wszystkich klatek,
`SyncInterval` = 3, `AllowsTearing` = 0, panel 3840×2160 przy 240 Hz.

### Co z tego wynika

**Gra jest zsynchronizowana co trzecie odświeżenie, czyli twardo 80 klatek na
sekundę.** Budżet klatki to 12,5 ms. Pierwotnie odczytałem brak limitu, bo
panel ma 240 Hz, a czasy klatek były rozrzucone od 11,3 do 13,8 ms zamiast
siedzieć w jednym punkcie; przeoczyłem `SyncInterval`. Poprawka odnotowana,
bo wnioski budowane na „braku limitu" byłyby fałszywe.

**Na tej maszynie po stronie GPU nie ma czego ugrać.** Karta zjada 6,70 ms
z 12,5 ms budżetu, czyli nudzi się przez prawie połowę każdej klatki, a
ścieżka prezentacji jest już optymalna — `Independent Flip` omija kompozycję
DWM. Cała grupa `graphics.*` w katalogu tweaków jest tu bezwartościowa i jest
to wniosek z pomiaru, nie z opinii.

**Procesor jest o włos od przekroczenia budżetu**: 11,89 ms z 12,5 ms, czyli
95%. To najmocniejsze dotąd uzasadnienie modułu masek — przy tak ciasnym
budżecie każda rywalizacja o rdzeń natychmiast kosztuje klatkę. Tłumaczy też,
dlaczego akurat maska dała −60,7% p99, a priorytet nie dał nic.

### Czego p99 nie widzi

Blok o p99 17,38 ms zawierał klatkę **375,65 ms**. Jedna na 2339 nie rusza
setnego percentyla, a gracz ją czuje. Aparat liczy teraz osobno klatki
powyżej 50 ms i przelicza je na minutę; próg wzięty z budżetu, bo 50 ms przy
12,5 ms to cztery zgubione klatki z rzędu.

### Nierozstrzygnięty wynik priorytetu wejścia-wyjścia

Pierwszy sparowany przebieg z hogiem dyskowym dał średnią sugerującą 85%
poprawy i **nie jest to prawda**. Rozbity na rundy: runda pierwsza 318,61 ms
wobec 18,22 ms, runda druga 18,76 ms wobec 31,31 ms — kierunek odwrócony.
Cały rzekomy zysk niósł pierwszy blok po starcie hoga, skażony rozgrzewaniem
pętli i dociąganiem świeżo zapisanego pliku. Protokół poprawiony: blok
rozgrzewkowy odrzucany, cztery rundy naprzemiennie AB/BA, mediana różnic
w parach zamiast średniej i jawny werdykt „nierozstrzygnięte", gdy kierunek
nie jest spójny między rundami.

### Ograniczenia aparatu, które zostają

Próg asercji w `LiveGameFrameTimeTests` nadal dopuszcza pogorszenie mediany
o połowę, bo służy wykrywaniu regresji, a nie dowodzeniu poprawy. Do
wykazania zysku mniejszego niż kilkadziesiąt procent to za mało i trzeba
będzie osobnej asercji.

## Pakiet dla zatwierdzonego tła od startu sesji i księga ograniczeń — 2026-09-11

Sekcja dopisana przez sesję pracującą nad stroną orkiestratora.

### Co się zmieniło

Wnioski z pomiarów wyżej były jednoznaczne, a kod ich nie odzwierciedlał:
jedyną dźwignią CPU, która ruszyła czas klatki, jest twarda maska, a trafiała
do procesu wyłącznie reaktywnie — po `SustainedSamples` × interwał (około
6 s) i tylko powyżej bramki obciążenia tła, której zwykłe użycie nie
przekracza. Aplikacje, na których ograniczenie użytkownik zgodził się
w planie, dostawały tylko BelowNormal + EcoQoS, czyli dokładnie to, co
w pomiarze nie dało nic.

Tryb „Ogranicz tło" (dawniej „Energooszczędne tło") nakłada teraz od
pierwszej sekundy sesji pełny pakiet, po jednej dźwigni na zasób:

| zasób | dźwignia | akcja | stan pomiaru |
|---|---|---|---|
| rdzenie | twarda maska ćwiartki | `ProcessAffinityAction` | −60,7% p99 (Valheim, pełne obciążenie) |
| częstotliwość | EcoQoS | bez zmian | niezmierzone osobno |
| pamięć | priorytet pamięci VeryLow | `ProcessMemoryPriorityAction` (nowa) | mechanizm potwierdzony na żywym procesie; wpływ na klatki niezmierzony |
| dysk | priorytet wejścia-wyjścia VeryLow | `ProcessIoPriorityAction` (sesja obok) | jw.; pierwszy sparowany pomiar nierozstrzygnięty |
| planista | BelowNormal | bez zmian | zero do ujemnego; do rozstrzygnięcia wariantem (a) |

Każda dźwignia to osobna akcja z własnymi identyfikatorami w planie
i w metadanych sesji — tak samo jak EcoQoS — więc wraca po sesji i po awarii
hosta. Maska, pamięć i dysk są best-effort: proces, który sam sobie zawęził
affinity albo już ma niski priorytet, zostaje z tym, co dostał, a sesja się
nie wywraca. Proces, który sam się zakończył, jest przy odtwarzaniu pomijany,
a nie liczony jako błąd — plan obiecuje użytkownikowi dokładnie to. Podgląd
planu opisuje przypięcie i priorytety, żeby zgoda dotyczyła tego, co się
naprawdę stanie.

Tryb „Tylko obniż priorytet" pozostaje tym, co mówi jego nazwa.

### Luka, która była: ograniczenia reaktywne nie wracały po awarii

`JournaledProBalanceActuator` zapisywał każdą akcję w dzienniku, a komentarz
obiecywał, że „odtwarzanie przy następnym starcie je znajdzie". Nie
znajdowało: `RecoverUserSessionAsync` czyta wyłącznie metadane sesji —
priorytet gry i zaplanowane aplikacje. Po awarii SessionHosta proces złapany
przez ProBalance zostawał w ćwiartce maszyny, z BelowNormal i z niskim
priorytetem wejścia-wyjścia, na stałe. Wiersz „priorytety i maski
procesora → dziennik + odzyskiwanie" w spisie planów mówił w tym miejscu
nieprawdę.

Zamknięcie: aktuator melduje każde ograniczenie i zwolnienie do
`IRestraintLedger`; orkiestrator trzyma je w `ActiveRuntime` obok
zaplanowanych aplikacji, zapisuje w punkcie kontrolnym
`BackgroundRestraintChanged` i odtwarza tą samą drogą. Przy wznowieniu żywej
sesji po restarcie oddaje je od razu — nie mają już nadzorcy, który by je
zdjął, gdy proces się uspokoi, a GameShift nie jest niczyim planistą na
stałe. Pętla staje na początku przywracania, żeby jej zwolnienia były częścią
sesji, a nie czymś po jej zamknięciu.

Test: `RestartedHostReleasesReactivelyRestrainedProcess` — prawdziwy
aktuator, prawdziwy proces liczący; symulowana jest tylko śmierć hosta przed
zwolnieniem.

### Dzieci dziedziczą maskę

Maska powinowactwa przechodzi na każdy proces utworzony po jej nałożeniu.
Przeglądarka, launcher i komunikator tworzą procesy przez cały czas gry;
dziennik cofa to, co zrobiono rodzicowi, i nic nie wie o dzieciach
urodzonych w ćwiartce. Po przywróceniu rodzica orkiestrator przegląda jego
potomków (jedna migawka Toolhelp na całe przywracanie) i tym, których maska
jest dokładnie równa ćwiartce, daje maskę rodzica — albo całą maszynę, gdy
rodzica już nie ma. Sygnatura jest dokładna, a kierunek ewentualnej pomyłki
bezpieczny: poszerzanie. Ten sam problem dotyczy dziedziczonego BelowNormal,
ale tam sygnatura nie jest jednoznaczna (Chrome sam obniża rendererom
priorytet), więc priorytetu dzieci nie ruszamy.

Test: potomek harnessu spawnowany 8 s po starcie, czyli już z maską, po
zakończeniu sesji ma maskę rodzica.

### Czego nie zrobiono i dlaczego

- **Zbiory CPU gry na dopełnieniu ćwiartki** — nie weszły. Gra ma dziś sześć
  rdzeni na wyłączność plus możliwość sięgnięcia po dwa pozostałe;
  preferencja by to odbierała bez pomiaru. Zostaje wariantem (b).
- **Usunięcie BelowNormal z pakietu** — nie bez pomiaru maska wobec
  maska + priorytet (wariant a). Pogorszenie po obniżeniu priorytetu nie
  dowodzi inwersji, dopóki nie pokaże się łańcucha zależności.
- **Priorytet pamięci w pętli reaktywnej** — aktuator zostawia te pola
  puste; wejdzie, gdy pomiar pod presją pamięci (wariant d) pokaże zysk.

### Pomiary do wykonania — wymagają gry na pierwszym planie

(a) maska wobec maska + BelowNormal; (b) ćwiartka wobec ćwiartka + zbiory CPU
gry na dopełnieniu; (c) hog dyskowy, priorytet wejścia-wyjścia Normal wobec
VeryLow — protokół poprawiony przez sesję obok; (d) hog pamięciowy około
10 GB, priorytet pamięci Normal wobec VeryLow; (e) ćwiartka wobec ćwiartka
+ EcoQoS — budżet mocy pakietu na i7-11700F jest wspólny, więc wolniejsze
hogi to wyższy boost rdzeni gry.

### Czego tym aparatem nie da się zmierzyć przy swobodnej grze

Dwa sparowane testy z rzędu skończyły się werdyktem „nierozstrzygnięte",
i przyczyna jest wspólna. Dane z testu maski na lekkim tle, cztery rundy
naprzemiennie po 30 sekund:

| wariant | r1 | r2 | r3 | r4 | rozrzut |
|---|---|---|---|---|---|
| tło wolne | 19,31 | 15,98 | 24,75 | 75,96 | 60 ms |
| tło w ćwiartce | 18,28 | 17,25 | 15,90 | 18,74 | 2,8 ms |

**Rozrzut wewnątrz jednego wariantu przewyższa szukany efekt.** Przy
swobodnej grze scena zmienia się między blokami, więc porównujemy sceny,
a nie warianty. Blok 75,96 ms mógł być zarówno dowodem, że maska
stabilizuje ogon, jak i skutkiem wejścia postaci do nowego obszaru —
czterema rundami tego nie rozdzielimy.

Wniosek dla każdego kolejnego pomiaru małego efektu:

1. **Powtarzalna scena**, nie swobodna gra. Postać stojąca w miejscu, ta
   sama lokacja i ten sam kierunek patrzenia, albo wbudowany benchmark.
2. **Więcej rund, nie dłuższe bloki.** Rozrzut jest między blokami, nie
   wewnątrz nich — 2200 klatek w bloku to już dużo, a p99 i tak skacze.
3. **Raportować rozrzut**, nie samą medianę różnic. „Mediana +4,94 ms"
   ukrywa rundę, w której wyszło −1,27 ms.

Osobna obserwacja, sugestywna i niepotwierdzona: wariant z maską trzymał
się w przedziale 15,9–18,7 ms w każdej rundzie, podczas gdy bez maski
skakał od 16 do 76 ms. Jeżeli to się potwierdzi na powtarzalnej scenie,
zyskiem wartym nazwania będzie stabilność ogona, a nie obniżenie mediany —
gracz czuje właśnie skoki.

### Priorytet wejścia-wyjścia: stan wiedzy

Mechanizm sprawdzony odczytem na żywym procesie: `NtSetInformationProcess`
z `ProcessIoPriority` przyjmuje wartość i odczyt ją potwierdza, na procesie
kończącym pracę zwraca `0xC000010A`. Zysk w czasie klatki **nie został
wykazany** — sparowany test z hogiem dyskowym dał różnice +0,60, +1,08,
+3,45 i −2,42 ms, czyli kierunek niespójny. Diagnoza: pojedynczy
sekwencyjny czytnik nie tworzy na NVMe rywalizacji, którą priorytet
mógłby rozstrzygnąć.

Zarówno maska, jak i priorytet wejścia-wyjścia **dziedziczą się na procesy
potomne** — zmierzone. Dlatego zwolnienie przegląda potomków i przywraca
tym, którzy mają dokładnie nasze wartości.

### Tańszy próbnik i krótszy interwał pętli — 2026-09-11

Punkty 11, 12 i 14 planu domknięte tym, co da się zmierzyć bez gry.
Próbnik pętli czyta teraz wszystkie procesy jednym wywołaniem
`NtQuerySystemInformation` zamiast otwierać uchwyt do każdego z osobna.
Zmierzone medianą z piętnastu przejść na maszynie deweloperskiej:

| droga | procesów | mediana | na proces |
|---|---|---|---|
| uchwyt na proces (`Process`) | 171 | 6,66 ms | 38,9 µs |
| jedno wywołanie systemowe | 316 | 3,42 ms | 10,8 µs |

Uchwyty widziały tylko 171 z 316 procesów, bo test nie jest podniesiony;
SessionHost jest, więc tam płacił za wszystkie. Całe przejście nadzorcy
z tym próbnikiem: 4,3 ms. Czasy startu z obu dróg są identyczne co do tiku
(sprawdzone dla każdego ze 171 wspólnych procesów), bo aktuator dopasowuje
próbki do tożsamości procesów przez równość tej wartości. Droga przez
uchwyty zostaje jako zapasowa.

Interwał pętli z 2 s na 1 s: przy 4,3 ms na przejście to 0,43% jednego
rdzenia, a hog jest łapany po około 3 s zamiast 6. Progi (`SustainedSamples`,
`RestrainAboveCores` i reszta) nie zmieniły się — krótszy jest wyłącznie czas
reakcji, nie warunek. Czy 3 s zamiast 6 przekłada się na czas klatki, wymaga
pomiaru z hogiem startującym w trakcie sesji; koszt własny pętli jest
zmierzony i mieści się w tym, co plan uznaje za akceptowalne.

### Przegląd adwersaryjny Codexa i utwardzenie odtwarzania — 2026-09-11

Na życzenie Damiana drugą parę oczu dał Codex CLI (`gpt-6-astra`,
`model_reasoning_effort=xhigh`, sandbox tylko do odczytu) nad zakresem
`725ef8e^..6cde271`. Dwanaście ustaleń; wszystkie sprawdzone w kodzie,
żadne nie okazało się fałszywe. Co z nich zrobiono:

| ustalenie | stan |
|---|---|
| meldunek do księgi dopiero po zmianach procesu — okno na awarię | **naprawione**: aktuator melduje przed pierwszą mutacją, z identyfikatorami wszystkich planowanych akcji; odtwarzanie akcji nigdy nienałożonej jest nieszkodliwe; przy odmowie priorytetu meldunek jest cofany |
| wykreślenie z księgi zależne tylko od priorytetu CPU | **naprawione**: każda kompensacja zwraca wynik, rekord znika dopiero po rozliczeniu całego pakietu, także potomków; co nie wróciło, ponawia orkiestrator przy zamknięciu sesji |
| wznowienie żywej sesji gubiło nieudane ograniczenia reaktywne | **naprawione**: przy błędzie zostają w `ActiveRuntime` i w punktach kontrolnych do skutku |
| wyścig migawek metadanych między monitorem a księgą | **naprawione**: migawka i zapis pod jedną bramką `_checkpointGate`, osobną od bramki orkiestratora |
| równoległe zatrzymania pętli (`DisposeAsync` i zamknięcie sesji) | **naprawione**: jedno wspólne zadanie zatrzymania, każdy wołający na nie czeka |
| potomkom przywracano tylko maskę | **naprawione i zmierzone**: potomek dziedziczy klasę priorytetu, priorytet I/O **i** priorytet pamięci (EcoQoS nie); przegląd przywraca maskę, I/O i pamięć |
| migawka potomków przed przywróceniem rodzica | **naprawione**: migawka po przywróceniu wszystkich rodziców, jedna na całe przywracanie |
| wnuk za zakończonym procesem pośrednim | **częściowo**: reguła sierot — proces, którego rodzic już nie istnieje, urodzony po nałożeniu ograniczenia, z dokładnie wartością ograniczenia; pełne śledzenie pochodzenia w trakcie sesji zostaje na później |
| błędy przywracania potomków niewidoczne | **naprawione**: liczą się jako błędy odtwarzania, sesja zostaje otwarta do ponownej próby |
| odtworzenie maski zależne od ponownego odczytu topologii | **naprawione**: `ProcessAffinityAction.ForRecovery` cofa do stanu z dziennika bez maski; maska sesji zapisana w metadanych (`BackgroundAffinityMask`) |
| poszerzanie dziecka, które samo wybrało maskę równą ćwiartce | **ograniczone**: filtr czasu startu — nic starszego niż ograniczenie nie mogło go odziedziczyć |
| aktuator dawał dzieciom całą maszynę zamiast maski rodzica | **naprawione**: wspólny `InheritedRestraintSweeper` dla orkiestratora i aktuatora; dziecko dostaje maskę, którą rodzic ma po przywróceniu, całą maszynę tylko gdy rodzica już nie ma |

Do tego gra nie może odziedziczyć ćwiartki po zatwierdzonym launcherze:
monitor drzewa gry zdejmuje ją z nowo odkrytych procesów gry.

**Zagrożenie w samych testach, znalezione przy okazji.** Testy sterujące
prawdziwym aktuatorem z pełnym próbnikiem ograniczały najcięższe procesy
maszyny deweloperskiej — edytor, serwer budowania, cudzy pomiar — a maska
i priorytety dziedziczą się na wszystko, co te procesy potem uruchomią.
W jednym przebiegu 99 procesów miało maskę 0xF, a host testów widział
4 procesory. Każdy taki test widzi teraz przez nadzorcę wyłącznie własne
procesy (`FilteredCpuProcessSource`). `LiveGameFrameTimeTests` celowo
zostaje przy pełnym próbniku, bo mierzy zachowanie na całej maszynie —
uruchamiać wyłącznie świadomie.

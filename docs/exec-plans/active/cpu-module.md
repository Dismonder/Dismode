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

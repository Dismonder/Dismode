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

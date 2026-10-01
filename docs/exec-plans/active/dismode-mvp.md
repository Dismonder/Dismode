# Plan wykonawczy: bezpieczne MVP Dismode

Status: aktywny — Bramy A–G ukończone w kodzie; System Optimizer 0.4.0 ma
usługę, bezpieczne IPC, pomiar A/B i recovery, lecz instalacja, restartowe
recovery i agresywne mutacje nadal oczekują na kontrolowaną VM oraz podpis
produkcyjny
Utworzono: 2026-07-28  
Źródło wymagań: pełna specyfikacja Dismode z załącznika zadania

## Cel

Dostarczyć natywne MVP Dismode na Windows 11, z rozdzielonym UI, SessionHost i SystemAgent, w którym każda zmiana systemowa jest uprzednio zapisana, weryfikowalna, idempotentna i możliwa do odzyskania po awarii.

## Stan początkowy i decyzja

Audyt wykazał pusty katalog bez repozytorium i istniejącego kodu. Dokumentacja może zostać przygotowana, ale inicjalizacja rozwiązania byłaby utworzeniem projektu od zera, czego przekazane instrukcje zabraniają bez wyraźnej decyzji.

Stan został ponownie zweryfikowany w trzech kolejnych przebiegach celu. Początkowo w katalogu istniały wyłącznie `docs/CODEX_CONTEXT.md` i ten plan.

2026-07-28 użytkownik wydał jednoznaczne polecenie utworzenia programu od zera. Blokada została usunięta, repozytorium zainicjalizowano i zrealizowano Bramę A.


## Bramy implementacyjne

### Brama A — fundament repozytorium

Status: ukończono

- potwierdzić lokalizację źródeł;
- zapisać `global.json`, rozwiązanie i minimalne projekty zgodne z zaakceptowaną architekturą;
- skonfigurować deterministyczny restore/build/test;
- dodać `AGENTS.md` do repozytorium, jeśli ma być lokalnym źródłem zasad;
- uruchomić pierwszy build bez logiki modyfikującej system.

Zrealizowano:

- `Dismode.sln` zawiera osiem rzeczywistych projektów, bez pustych atrap;
- .NET SDK przypięty do `10.0.302`;
- stabilny Windows App SDK przypięty centralnie do `2.3.1`;
- UI jest natywne, unpackaged, x64 i działa jako `asInvoker`;
- SessionHost i SystemAgent mają diagnostyczne entrypointy bez mutacji;
- Git zainicjalizowano na gałęzi `main`.

Dowód ukończenia:

- `dotnet restore`, `dotnet build` i test bazowy kończą się kodem 0;
- rozwiązanie zawiera wyłącznie uzgodnione procesy i biblioteki;
- brak ostrzeżeń o niezgodnych target frameworkach.

### Brama B — czysta domena bezpieczeństwa

Status: ukończono

- model tożsamości procesu odporny na PID reuse;
- jawna maszyna stanów sesji;
- typowane `ReversibleAction` z prepare/validate/apply/verify/compensate;
- polityka Hard Safety Policy;
- trójstronna decyzja przywracania;
- idempotency keys i wersjonowanie kontraktów.

Dowód ukończenia:

- testy jednostkowe wszystkich przejść stanu, konfliktów i blokad;
- domena nie zależy od WinUI, SCM ani fizycznego systemu plików.

Zrealizowano:

- `ProcessIdentity` wymaga PID, czasu startu, pełnej ścieżki, SHA-256, SID i session ID;
- pełna, jawna macierz stanów sesji blokuje pomijanie snapshotu i weryfikacji;
- `IReversibleAction<TState>` definiuje prepare/validate/apply/verify/compensate/verify compensation;
- `HardSafetyPolicy` blokuje wszystkie mutacje chronionych celów oraz mutacje bez zweryfikowanego recovery;
- `ThreeWayReconciler` przywraca tylko, gdy stan aktualny nadal odpowiada stanowi zastosowanemu;
- kontrakty mają zamknięty `CommandKind`, wersję protokołu i silny `IdempotencyKey`;
- 19/19 testów, w tym każda para stanów sesji, zakończyło się powodzeniem.

### Brama C — journal, snapshot i recovery na adapterach testowych

Status: ukończono

- niezależny append-only journal z sekwencją, kontrolą integralności i flush przy punktach krytycznych;
- zapis intencji oraz oryginalnego stanu przed apply;
- checkpointy sesji;
- odtwarzanie niedokończonej sesji;
- idempotentny rollback w odwrotnej kolejności zależności;
- crash injector dla kontrolowanych adapterów.

Dowód ukończenia:

- testy przerwania przed/po każdym krytycznym zapisie;
- ponowne recovery prowadzi do tego samego wyniku;
- żadna wykonana akcja nie istnieje bez wcześniejszego wpisu journalu.

Zakaz przejścia dalej:

- brak operacji na produkcyjnych usługach i aktywnym planie zasilania przed zaliczeniem tej bramy.

Zrealizowano:

- niezależny JSONL journal zapisuje sekwencję, poprzedni hash i SHA-256 rekordu;
- `WriteThrough`, `FlushAsync` i `Flush(true)` kończą trwały zapis przed mutacją;
- journal wykrywa zmianę rekordu, przerwany ostatni zapis i uszkodzenie łańcucha;
- stan oryginalny i zamierzony jest zapisany przed `ApplyAsync`;
- session checkpointy współdzielą ten sam łańcuch integralności;
- crash injection obejmuje przed apply, po apply i w trakcie compensation;
- recovery jest idempotentne, zachowuje stan zewnętrzny i raportuje konflikt;
- wiele działań jest przywracanych w odwrotnej kolejności zastosowania;
- 10/10 testów recovery oraz 19/19 testów jednostkowych zakończyło się powodzeniem.

### Brama D — Faza 0: bezpieczna obserwacja i IPC

Status: ukończono

- enumeracja procesów i usług w trybie read-only;
- odczyt konfiguracji usług bez zatrzymywania;
- UI testowe, SessionHost i testowy SystemAgent;
- dwa kanały Named Pipes z ACL;
- ograniczone, wersjonowane kontrakty;
- replay protection, walidacja SID, request ID, timestampu i rozmiaru wiadomości.

Dowód ukończenia:

- integracyjne testy komunikacji i odmowy nieautoryzowanych żądań;
- brak lokalnego portu TCP/HTTP;
- smoke test UI pokazuje dane z testowego agenta.

Zrealizowano:

- Protobuf generuje zamknięty serwis `DismodeDiagnostics` oraz wersjonowane metadata;
- Kestrel nasłuchuje przez HTTP/2 wyłącznie na dwóch named pipes, bez socketu TCP;
- chroniony ACL zawiera konkretny SID, LocalSystem i lokalnych administratorów, bez `Everyone` i `Authenticated Users`;
- limit wiadomości wynosi 1 MiB, discovery maksymalnie 500 rekordów;
- walidacja odrzuca złą wersję, SID, timestamp, session ID, replay i przepełnienie cache;
- SessionHost enumeruje procesy, a SystemAgent odczytuje stan, typ i zależności usług bez ich zmiany;
- `SessionSimulator` wykonał `GetStatus` i `Discover` po obu rzeczywistych kanałach;
- build: 0 ostrzeżeń; testy: 37/37; format: kod 0.

Pozostałe utwardzenie IPC, w tym dowód podpisu SessionHost, należy do Bramy H i wymaga podpisanego wydania z chronionej lokalizacji.

### Brama E — kontrolowane akcje procesów

Status: ukończono

- obserwacja i telemetria procesów;
- priorytet i EcoQoS przez adaptery Windows;
- graceful close bez wymuszonego kill w trybie bezpiecznym;
- restartowalność aplikacji opisana uczciwym poziomem;
- ręczny profil gry i śledzenie drzewa procesu.

Dowód ukończenia:

- adapter testowy i testy tożsamości przed każdą zmianą;
- restore po normalnym końcu, crashu i powtórzeniu polecenia;
- brak ingerencji w anti-cheat i chronione procesy.

Zrealizowano:

- pełna tożsamość procesu jest ponownie sprawdzana na tym samym uchwycie bezpośrednio przed mutacją;
- akcje priorytetu i EcoQoS implementują prepare/apply/verify/compensate i korzystają z append-only journalu;
- dozwolone redukcje priorytetu są ograniczone do `Normal → BelowNormal` i `BelowNormal → Idle`;
- EcoQoS używa udokumentowanego `SetProcessInformation(ProcessPowerThrottling)` i odczytuje stan przed zmianą;
- graceful close używa wyłącznie standardowego żądania zamknięcia, ma timeout i nigdy nie wywołuje `Kill`;
- restart wymaga profilu `Restartable`, zgodnego SID i niezmienionego SHA-256;
- ręczny profil EXE jest parametryzowany, przechowywany w User Database i uruchamiany bez shella;
- Toolhelp snapshot śledzi drzewo potomne, a telemetria próbkuje tylko wskazany, zweryfikowany proces;
- test timeoutu potwierdza, że odrzucenie zamknięcia pozostawia aplikację działającą i nie tworzy duplikatu;
- 14/14 testów integracyjnych jest zielonych; żadna akcja nie dotknęła obcego procesu.

### Brama F — zatwierdzone usługi i plan zasilania

Status: implementacja ukończona; produkcyjna aktywacja zablokowana do walidacji VM

- pełny snapshot konfiguracji i stanu usługi;
- analiza zależności, shared host i trigger-start;
- wyłącznie zatwierdzona lista niskiego ryzyka;
- własny profil zasilania bez modyfikacji profilu użytkownika;
- automatyczne recovery po restarcie usługi/hosta.

Dowód ukończenia:

- najpierw testowa usługa, potem kontrolowana VM;
- 100% zmian przywróconych w macierzy crash injection;
- brak trwałego ustawiania usługi jako `Disabled`.

Zrealizowano:

- pełny snapshot usługi obejmuje status, start type, service type, konto, binarną ścieżkę, delayed auto-start, triggery, PID, zależności i działające usługi zależne;
- katalog ochronny oraz Hard Safety blokują usługi krytyczne, driver, shared-process, trigger-start i cele z działającymi dependent services;
- zatrzymanie wymaga krótkotrwałej zgody powiązanej z sesją i nigdy nie zmienia start type;
- własny schemat zasilania otrzymuje zaplanowany GUID, powstaje jako kopia aktywnego schematu, jest aktywowany i usuwany po przywróceniu bez modyfikacji planu użytkownika;
- specjalizowana decyzja recovery usuwa częściowy klon po awarii między duplicate i activate;
- konflikt z ręczną zmianą planu lub konfiguracji usługi zachowuje stan zewnętrzny;
- macierz checkpointów apply i compensation jest zielona na adapterach symulacyjnych;
- dodano detekcję form factoru `DeviceFormFactor` (`Desktop`, `Laptop`, `Unknown`) i `WindowsDeviceFormFactorDetector`;
- `HardSafetyPolicy` oraz `ActivateManagedPowerProfileAction` blokują mutację planu zasilania na komputerach stacjonarnych (`DesktopPowerPlanPreserved`), zabezpieczając aktywny plan użytkownika;
- produkcyjne P/Invoke SCM/PowrProf zostało użyte na hoście tylko w trybie read-only; żaden plan nie został przełączony i żadna usługa nie została zatrzymana.

Pozostała ręczna brama:

- własna testowa usługa, kontrolowana VM, restart hosta i potwierdzenie recovery przed podłączeniem mutujących RPC.

### Brama G — MVP UI, historia i diagnostyka

Status: ukończone dla rzeczywistych, odwracalnych akcji procesów użytkownika

#### Iteracja wizualna — gamingowy interfejs desktop

Status: ukończono lokalnie i wdrożono

- zachować wszystkie istniejące przepływy, nazwy automatyzacji i granice
  bezpieczeństwa;
- wprowadzić wspólny system powierzchni, akcentów, typografii, kart i
  przycisków dla wszystkich ekranów;
- przebudować pulpit wokół głównej akcji Trybu gry;
- usunąć ściskanie nagłówków i poleceń przy mniejszym oknie;
- zweryfikować wizualnie wydanie przy szerokim i węższym rozmiarze okna.

Zrealizowano:

- granatowo-cyjanowy system powierzchni, akcentów, typografii, kart i
  przycisków z wariantem jasnym oraz wysokiego kontrastu;
- nowy hero pulpitu i mocniejsza hierarchia głównej akcji Trybu gry;
- adaptacyjna nawigacja oraz dynamiczny `CommandBar` biblioteki zamiast
  pięciokolumnowego nagłówka ściskającego tytuł;
- ciemny natywny pasek tytułu z dopasowanymi przyciskami systemowymi;
- build Debug/Release bez ostrzeżeń, publikacja i responsywne uruchomienie
  jednego UI oraz obu hostów.

- natywne WinUI 3: pulpit, plan, aktywna sesja, przywracanie, profile, historia i diagnostyka;
- jasny/ciemny motyw, dostępność klawiatury i narratora;
- statusy nieprzekazywane wyłącznie kolorem;
- retencja i redakcja danych;
- UI pozostaje niepodwyższone i odłączalne od aktywnej sesji.

Zrealizowano:

- natywne widoki pulpitu, profili, planu i sesji, historii oraz diagnostyki;
- automatyczna biblioteka Steam, Epic, GOG, Roblox i dostępnych instalacji
  Xbox oraz ręczne profile EXE z SHA-256;
- SQLite v5 przechowuje priorytet gry, reguły
  `zamknij / obniż priorytet / obniż + EcoQoS / ignoruj` per gra,
  preferencje HUD oraz rzeczywiste statystyki zakończonych sesji;
- sesyjny RPC prepare/approve/start/get-active/restore przez chroniony pipe użytkownika;
- plan ponownie sprawdza EXE, pokazuje opcjonalne procesy bez automatycznego
  zaznaczania nowych kandydatów, zapisuje stan przed zatwierdzoną zmianą i
  uruchamia grę bez shella albo dołącza do jednego już działającego, w pełni
  zgodnego procesu. Dopiero potem wykonuje zatwierdzone akcje tła kolejno;
- wybrane aplikacje są łagodnie zamykane i odtwarzane, procesy tła otrzymują
  `BelowNormal` oraz opcjonalne EcoQoS w osobnych transakcjach. Gra zachowuje
  domyślne `Normal`; `AboveNormal` lub `High` wymaga ręcznego, eksperymentalnego
  wyboru i ostrzeżenia, a `Realtime` jest zablokowany;
- osobne, zamknięte RPC pozwala natychmiast zakończyć wyłącznie ponownie
  zweryfikowane procesy okna aktywnej gry, bez przyjęcia dowolnego PID;
- zakończenie sesji nie zamyka gry, a restart SessionHost odzyskuje jej
  monitorowanie z journalu także po wyjściu launchera, jeśli nadal działa
  zidentyfikowany proces potomny gry;
- produkcyjna akcja `High` zapisuje snapshot, ustawia rzeczywistą klasę
  procesu przez Windows, odczytuje ją ponownie i przywraca poprzedni stan;
- oficjalny PresentMon 2.5.1 (MIT) jest przypiętym składnikiem wydania;
  SessionHost uruchamia go automatycznie dla śledzonych PID i oblicza FPS/czas
  klatki z prawdziwych zdarzeń ETW, a brak świeżych danych pozostaje jawny;
- HUD jest widoczny wyłącznie przy foreground PID śledzonej gry, a
  przełącznik, krycie, skala i cztery rogi są trwale zapisane. Krycie
  steruje natywnym alpha całego okna i jest potwierdzane odczytem Windows,
  więc nie wygasza już XAML do ciemnego tła;
- domyślny przegląd używa układu Bento i Mica, a HUD ma uproszczony układ
  Bento 344×152 z oddzielnymi kaflami FPS i frametime;
- zakończenie sesji zapisuje liczbę próbek, średni FPS/ms, najniższą próbkę
  FPS oraz najwyższą próbkę ms; UI nie nazywa tego benchmarkiem ani 1% low;
- SessionHost wymaga zgody UAC, natomiast Launcher, SystemAgent i UI pozostają
  niepodwyższone;
- `Dismode.exe` uruchamia stałe komponenty z jednego katalogu i nie dubluje już działających procesów;
- skrót `Dismode` utworzono na pulpicie użytkownika;
- naprawiono publikowanie luźnych XBF/PRI, którego brak powodował awarię WinUI `0xc000027b`.
- UI pokazuje stan `Offline`, odzyskuje oba pipe’y bez restartu i unieważnia przygotowany plan, jeżeli SessionHost utraci jego stan;
- nazwy elementów profili, planu i historii nie używają już nazw klas w drzewie automatyzacji;
- aktywny monitor nie hashuje dużego EXE w pętli: pełną tożsamość sprawdza przy granicach bezpieczeństwa, a w pętli PID, czas startu, ścieżkę, SID i session ID.
- procesy oraz usługi są klasyfikowane jako wymagane systemowe, infrastruktura
  gry albo opcjonalne; system, launchery, anti-cheat, Discord/OBS i Dismode
  nie trafiają do automatycznego planu;
- procesy pomocnicze z rozpoznanego katalogu instalacyjnego wybranej gry są
  ponownie klasyfikowane jako infrastruktura gry i nie trafiają do planu;
- nowa ikona PNG/ICO jest osadzona w Launcherze, UI, oknie i skrócie;
- opublikowane wydanie protokołu v4 działa w
  `artifacts\Dismode-App`, a skrót znajduje się na Pulpicie.
- kafelki Play w Bibliotece są rzeczywistymi przyciskami: przygotowują i
  uruchamiają zweryfikowany profil przez SessionHost, zachowują priorytet
  `Normal`, stosują wcześniej zatwierdzone reguły procesów tej gry i przechodzą
  do widoku aktywnej sesji bez bezpośredniego uruchamiania EXE przez UI.
  SessionHost dopasowuje reguły po pełnej ścieżce EXE i ponownie wykonuje
  klasyfikację oraz pełną walidację tożsamości.

Dowód ukończenia:

- build x64, smoke test UI i test ponownego połączenia;
- ręczna checklista skalowania, wysokiego kontrastu i Narratora;
- zamknięcie UI nie przerywa sesji agenta.

### Brama H — utwardzenie i akceptacja MVP

Status: częściowo ukończone; walidacja usług/planów zasilania wymaga VM

- testy recovery po restarcie, wylogowaniu, crashu i konflikcie zewnętrznym;
- testy IPC: replay, duże/niepoprawne wiadomości, obcy SID i downgrade;
- pomiar narzutu Dismode;
- raport sesji z rzeczywistym FPS/ms z PresentMon albo jawnym stanem braku danych,
  bez fałszywych obietnic;
- dokumentacja instalacji i ręcznego awaryjnego przywrócenia.

Dowód ukończenia:

- każda operacja ma journal;
- 100% kontrolowanych zmian wraca do uzgodnionego stanu;
- brak trwałej zmiany bez zgody;
- wszystkie automatyczne akcje przechodzą Hard Safety Policy.
- raport FPS/ms jest wdrożony w SQLite v5 i Historii; test obejmuje pełny
  przepływ próbka → finalizacja → zapis → odczyt oraz migrację v4→v5.

## Decyzje architektoniczne

- Recovery i journal powstają przed produkcyjnymi mutacjami systemu.
- UI nie jest zaufaną granicą i nie posiada uprawnień administratora.
- Adaptery Windows są odseparowane od domeny, aby crash injection nie dotykał systemu gospodarza.
- SQLite nie jest jedynym źródłem recovery.
- Zakres MVP nie obejmuje AI, chmury, sterownika, trwałych tweaków rejestru ani automatycznej manipulacji urządzeniami.

## Walidacja wykonana do tej pory

- potwierdzono 0 elementów w katalogu początkowym;
- potwierdzono brak `.git`, solution i projektów;
- potwierdzono .NET SDK `10.0.302`;
- potwierdzono Windows SDK `10.0.26100.0`;
- potwierdzono Visual Studio Community `18.7.4`;
- potwierdzono obecność szablonów WinUI 3 w instalacji Visual Studio;
- w trzeciej weryfikacji potwierdzono nadal `0` plików `.sln`, `.slnx`, `.csproj`, `.props`, `.targets` i brak `.git`;
- użytkownik zatwierdził inicjalizację programu od zera;
- `dotnet restore Dismode.sln`: kod 0;
- `dotnet build Dismode.sln --configuration Debug --no-restore`: kod 0, 0 ostrzeżeń, 0 błędów;
- `dotnet test Dismode.sln --configuration Debug --no-build`: kod 0, 1/1 test zaliczony;
- `dotnet format Dismode.sln --verify-no-changes --no-restore`: kod 0;
- SessionHost i SystemAgent: smoke test, kod 0, jawny stan bez mutacji;
- UI WinUI 3: proces uruchomiony, łagodnie zamknięty, kod 0.
- po Bramie B: build kod 0, 0 ostrzeżeń; testy 19/19; format kod 0.
- po Bramie C: build kod 0, 0 ostrzeżeń; testy 29/29; format kod 0.
- po Bramie D: build kod 0, 0 ostrzeżeń; testy 37/37; format kod 0.
- smoke IPC Bramy D: dwa hosty uruchomione z dokładnie zbudowanych plików; status i ograniczone discovery zwrócone po obu named pipes; stderr pusty; procesy testowe zamknięte.
- po akcjach procesów: pełna regresja 43/43; po profilach, SQLite, drzewie i telemetrii testy integracyjne 14/14 (łącznie 49 testów).
- audit NuGet odrzucił podatny `SQLitePCLRaw 2.1.11`; przypięto poprawioną bibliotekę natywną `2.1.12` zamiast wyciszać NU1903.
- po adapterach usług i zasilania: restore kod 0, build 0 ostrzeżeń, pełna regresja 61/61, format kod 0;
- po bezpiecznej orkiestracji sesji i launcherze: build Release 0 ostrzeżeń, pełna regresja 65/65;
- smoke opublikowanego `Dismode.exe`: launcher kod 0, jedno responsywne okno, dokładnie po jednym SessionHost/SystemAgent, oba pipe’y zgodne z protokołem; ponowne kliknięcie nie utworzyło duplikatów;
- ręczny reconnect: bez restartu UI oba hosty przeszły z `Offline` do `ReadOnly`; PID UI pozostał ten sam;
- rzeczywista sesja Roblox zapisała `SnapshotComplete`, `GameLaunched` i `SessionActivated`; po zatrzymaniu hostów proces gry pozostał uruchomiony, a SessionHost odzyskał sesję z journalu;
- po poprawkach dostępności i lekkiego monitoringu: build Release 0 ostrzeżeń, format kod 0, pełna regresja 67/67;
- audit NuGet `--vulnerable --include-transitive`: brak znanych podatnych pakietów we wszystkich 14 projektach;
- pomiar ujawnił i usunął powtarzane hashowanie EXE: przed poprawką SessionHost zużywał około 4,4% całej 16-wątkowej maszyny; test integracyjny potwierdza brak pełnych capture/hash w aktywnej pętli;
- odczyt aktywnego schematu i pełnej konfiguracji usługi wykonano bez mutacji hosta;
- 6 scenariuszy profilu zasilania obejmuje apply/restore, częściowy duplicate, konflikt zewnętrzny, brak ownership oraz wszystkie checkpointy transakcji i recovery.
- po wdrożeniu rzeczywistego Trybu gry build Debug i Release zakończyły się
  bez ostrzeżeń; testy zostały pominięte na jawne życzenie użytkownika;
- opublikowany protokół v4 zwrócił pełne discovery 268 procesów i 304 usług,
  oba pipe’y działały, a SessionHost raportował brak aktywnej sesji;
- biblioteka na komputerze użytkownika zawiera 9 aktualnych gier; skrót
  Pulpitu wskazuje na bieżący launcher, a Roblox nie został przerwany.
- bieżące wydanie dołącza do jednej zgodnej, już uruchomionej gry bez
  duplikatu i chroni jej procesy pomocnicze na podstawie katalogu instalacji;
  build Debug/Release zakończył się bez ostrzeżeń, a journal pozostał czysty.
- produkcyjny plan wystawia osobny, odwracalny EcoQoS po `BelowNormal`; baza
  została zmigrowana do v3 bez utraty 9 profili;
- kontrolowany test produkcyjnej `RuntimeProcessEcoQosAction` przeszedł 1/1:
  własny process harness otrzymał EcoQoS, został zweryfikowany, przywrócony
  z journalu, a ponowne recovery nie wykonało drugiej mutacji;
- produkcyjna akcja priorytetu gry przeszła 1/1: niezależny od niej odczyt
  Windows zwrócił `High`, a recovery przywróciło `Normal`;
- test drzewa launchera przeszedł 1/1: potomna gra pozostała aktywna po
  wyjściu rodzica, została odzyskana po restarcie SessionHost i łagodnie
  zamknięta; sąsiednie testy lekkiego monitoringu/recovery przeszły 2/2;
- parser CSV v2 PresentMon przeszedł 3/3 wąskie testy, oficjalny strumień
  2.5.1 został potwierdzony lokalnie, a przypięty artefakt przeszedł kontrolę
  rozmiaru i SHA-256;
- wykrywanie Xbox analizuje tylko dostępne manifesty `MicrosoftGame.Config`
  i pomija chronione obrazy bez uruchamialnego EXE zamiast tworzyć martwy
  profil.
- naprawiono mylenie limitu odpowiedzi z całkowitą liczbą elementów:
  diagnostyka zwraca osobne total counts, UI nie zatrzymuje się na 100, a
  pełna lista usług obejmuje również zatrzymane rekordy;
- test kompletności usług przeszedł 1/1; bieżący Windows i Dismode zwróciły
  te same 316 usług. Eksport użytkownika miał jeden nieaktualny wpis
  `Usługa pomocy ZTDNS`;
- nowe wydanie z kopią `Dismode-App.backup-20260729-135605` uruchomiło
  responsywne UI oraz zwróciło 287 procesów i 316 usług bez ucięcia; Roblox
  pozostał uruchomiony.
- po podłączeniu przycisków Play build Debug/Release zakończył się bez
  ostrzeżeń; wydanie z kopią `Dismode-App.backup-20260729-191034`
  uruchomiło jeden UI i oba hosty, SessionHost raportował brak aktywnej
  sesji, a journal był czysty. Celowo nie klikano prawdziwej gry podczas
  smoke testu.
- po dodaniu historii rzeczywistych statystyk sesji build Debug/Release
  zakończył się bez ostrzeżeń, format kodem 0, a pełna regresja 90/90
  (27 Unit, 12 Recovery, 48 Integration, 3 Security);
- po naturalnym zakończeniu gry journal był czysty (654 rekordy). Wydanie
  opublikowano do `artifacts/Dismode-App`, poprzednie zachowano jako
  `Dismode-App.backup-20260729-221808`; smoke potwierdził jedno
  zmaksymalizowane UI, SessionHost `Ready`, SystemAgent `ReadOnly`, SQLite v5
  i brak HUD nad pulpitem bez aktywnej gry.
- po podłączeniu zapisanych reguł do szybkiego Play build Debug/Release
  zakończył się bez ostrzeżeń, format kodem 0, a pełna regresja 94/94
  (27 Unit, 12 Recovery, 52 Integration, 3 Security). Kontrolowany test
  procesu potwierdził rzeczywiste `Normal → BelowNormal → Normal`;
  `Dismode-App.backup-20260729-224628` zachowuje poprzednie wydanie.
  Końcowy smoke potwierdził zmaksymalizowane UI PID `3088`, gotowe pipe'y,
  brak aktywnej sesji i brak uruchomionej gry lub PresentMon.
- Safety Release 0.1.1 ustawił `Normal` we wszystkich domyślnych ścieżkach,
  odłączył zapisany `High/AboveNormal` od szybkiego Play, usunął automatyczny
  wybór nowych procesów tła oraz doprecyzował komunikaty EcoQoS, working set
  i usług. Build Debug: 0 ostrzeżeń; format: kod 0; pełna regresja: 95/95
  (27 Unit, 12 Recovery, 53 Integration, 3 Security). Nie uruchamiano UI,
  instalatora, usług ani gier.
- po zmianie launch-first i Bento UI build Debug zakończył się bez ostrzeżeń,
  format kodem 0, a pełna regresja 95/95. Test integracyjny potwierdził
  `GameLaunched` przed `ProcessesApplied`; rzeczywista gra i okna nie były
  uruchamiane przez Codex.

## Ryzyka

- Memory Optimizer 0.3.0 ma odizolowane rozwiązanie GPL, usługę LocalSystem,
  tray użytkownika, bezpieczny pipe per SID oraz neutralny eksport aktywnej
  gry. Build obu rozwiązań jest bez ostrzeżeń; regresja główna przeszła
  210/210, komponent 31/31 z jednym celowo pominiętym testem agresywnym.
- Finalna instalacja agregatu pozostaje zablokowana przez niedokończony wpis
  recovery `f34113f1-6660-4745-a726-d04a59fcf537`. Bramy nie pominięto i nie
  uruchamiano operacji pamięci na hoście.

- rozpoczęcie scaffoldingu w błędnym katalogu;
- użycie niezatwierdzonej wersji Windows App SDK;
- połączenie recovery z główną bazą i utrata niezależności awaryjnej;
- zbyt wczesne testowanie operacji na realnych usługach;
- mylenie powodzenia apply z pełną możliwością restore;
- nieuwzględnienie zmian stanu wykonanych przez system lub użytkownika podczas sesji.

## Następna czynność

Na kontrolowanej VM przejść instalację/aktualizację 0.3.0→0.4.0, restart
SystemAgent/systemu, crash-injection każdego checkpointu oraz pełny uninstall
z trójstronnym recovery. Nie rozszerzać katalogu `Supported`, dopóki nowy
adapter nie ma niezależnego readbacku i zielonego restore na macierzy VM.
Równolegle pozostaje ręczna checklista 1080p/4K, DPI 100–200%, wysokiego
kontrastu i Narratora.

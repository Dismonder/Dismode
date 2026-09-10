# Plany wykonawcze GameShift

Spis wszystkich planów i uczciwy stan każdego z nich. Aktualizowany razem
z wydaniem.

Stan na: 2026-09-10, wydanie **0.6.3**

## Zasada

Plan leży w `active/`, dopóki został z niego choć jeden krok do zrobienia.
Kiedy ostatnia brama się zamknie, ląduje w `completed/` — razem z dowodami,
nie z deklaracją. „Zaimplementowane" i „zobaczone jak działa" to dwie różne
rzeczy i w tych dokumentach nigdy nie są mieszane.

## Aktywne

| Plan | Czego dotyczy | Stan | Co blokuje zamknięcie |
|---|---|---|---|
| [`render-mods-module.md`](active/render-mods-module.md) | moduł OptiScaler, dodatki per gra, DLSS Neural Rendering | kod kompletny, wydany w 0.5.2 | Neural Rendering nigdy nie widziany działający w grze; układ metody 1 dla RE9 niepotwierdzony |
| [`cpu-module.md`](active/cpu-module.md) | powinowactwo CPU, ProBalance, zamiennik Process Lasso | **przebudowany i zmierzony — p99 lepsze o 26% (pełne obciążenie) i 10,6% (umiarkowane)** | ścieżki hybrydowa (P/E) i wielo-CCD nadal nigdy nie wykonały się na prawdziwym sprzęcie |
| [`update-platform-installer-hardening.md`](active/update-platform-installer-hardening.md) | kanał aktualizacji, dzielenie instalatora, podpisy | 0.5.2 zbudowana i podpisana **certyfikatem testowym** | produkcyjny certyfikat Authenticode; smoke update/uninstall na VM |
| [`system-optimizer-0.4.0.md`](active/system-optimizer-0.4.0.md) | osobny optimizer A/B, usługa `GameShiftSystemAgent` | implementacja i lokalna regresja zakończone | macierz VM z recovery po restarcie; podpis produkcyjny |
| [`gameshift-mvp.md`](active/gameshift-mvp.md) | bramy A–G całego MVP, transakcyjność, recovery | bramy A–G ukończone w kodzie | instalacja i restartowe recovery na kontrolowanej VM; checklista dostępności (DPI, kontrast, Narrator) |

## Ukończone

`game-metadata-refresh` (0.1.7) · `launch-latency-bento-ui` (0.1.7) ·
`memory-optimizer-ui` (0.3.0) · `aggressive-mode-overlay` · `fps-overlay-v2` ·
`installer-exe` · `one-click-saved-optimization` · `overlay-settings` ·
`presentmon-migration` · `safety-release-0.1.1` ·
`session-performance-history`

## Plan rozwoju — kolejność

Nie po ważności, tylko po tym, co odblokowuje co. Każda pozycja kończy się
faktem, który można pokazać.

### Zakres: decyzja z 2026-09-10

Funkcje dodatkowe OptiScalera — DLSS Neural Rendering i katalog wymagań per gra
— zostają **zaparkowane** na życzenie użytkownika. Kod jest kompletny i wydany
w 0.5.2, runbook potwierdzenia leży w planie modułu i czeka na maszynę `ERYK`.
Nic z tego nie jest już motorem rozwoju.

Rozwój idzie w wydajność i realne działanie: żeby to, co GameShift deklaruje,
dało się zmierzyć i zobaczyć.

### Teraz: potwierdzić to, co już napisane

Cztery moduły są „gotowe w kodzie" i żaden nie został zobaczony w działaniu
na docelowym sprzęcie. To jest największy dług projektu — nie brak funkcji.

1. ~~**OptiScaler w RE9 na `ERYK`**~~ — zaparkowane, patrz wyżej.
2. **Nakładka FPS na 0.5.2** — czy pokazuje liczby przy grze odpalonej spoza
   GameShifta, czy nie migocze przy alt-tabie, czy wykres mieści się w ramce.
   Przebudowana w 0.5.0 i od tego czasu nieoglądana.
3. ~~**Moduł CPU na grze ograniczonej procesorem**~~ — **zrobione 2026-09-10
   na 7 Days To Die.** Pierwszy pomiar wyszedł przeczący i doprowadził do
   przebudowy: priorytet nie daje nic, twarda maska powinowactwa daje 26%
   przy pełnym obciążeniu i 10,6% przy umiarkowanym. Szczegóły w planie.

### Potem: sprzęt, którego tu nie ma

4. **Ścieżka hybrydowa P/E i wielo-CCD** — cały `CpuAffinityPolicy` ma dla nich
   gałęzie, które nigdy się nie wykonały. Potrzebny Core Ultra 7 265K
   z `ERYK` albo Ryzen z dwoma CCD.
5. **Macierz VM** — instalacja, restart usługi, crash-injection każdego
   checkpointu, pełny uninstall z recovery. Wspólna brama dla
   `system-optimizer-0.4.0` i `gameshift-mvp`.

### Potem: bramy wydania publicznego

6. **Produkcyjny certyfikat Authenticode.** Dziś każdy build podpisuje się
   lokalnym `CN=GameShift Development` i wymaga flagi
   `-AllowTestCodeSigningCertificate`. Bez prawdziwego certyfikatu żadna
   wersja nie może wyjść poza te dwa komputery.
7. **Checklista dostępności** — 1080p/4K, DPI 100–200%, wysoki kontrast,
   Narrator. Ręczna, nigdy nieprzeprowadzona.

### Na końcu: rozbudowa

Nic z poniższych nie zaczyna się przed punktem 3. Dokładanie funkcji do
modułu, o którym nie wiemy, czy działa, tylko powiększa dług.

- kolejne gry w katalogu wymagań OptiScalera (N6);
- drabina eskalacji ProBalance: priorytet → ECO QoS → mocniejsze odsuwanie;
- priorytet pamięci przez `SetProcessInformation`, powinowactwo per wątek;
- adaptywny interwał nadzorcy CPU;
- zapisywane per gra ustawienia CPU;
- wsparcie powyżej 64 procesorów logicznych.

## Znane luki w testach

Zapisane tu, żeby nie udawać, że ich nie ma.

- ~~**Wpięcie orkiestratora sesji** nie ma testu automatycznego.~~
  **Zamknięte 2026-09-10.** `ProBalanceToggleReachesTheCpuModule` sprawdza, że
  przełącznik faktycznie powołuje pętlę ograniczania z prawdziwymi
  współpracownikami, a przy wyłączonym nie sięga po żadne procesy — i że po
  zamknięciu sesji pętla stoi. Deterministycznie, bez wyścigu z pętlą i bez
  prawdziwego obciążenia: dwa przypadki w sekundę. Sama logika pętli ma własne
  testy sterujące `TickAsync` bezpośrednio, więc podział jest czysty.
- **Progi ProBalance** są częściowo wystrojone pomiarem: bramka obciążenia
  została przebudowana z „procent całej maszyny" na „rdzenie zajęte przez tło"
  i ustawiona na podstawie zmierzonych 0,07 rdzenia przy cichej maszynie wobec
  4,04 przy obciążeniu, w którym zysk wystąpił. `SustainedSamples`,
  `MinimumRestraint` i `Cooldown` nadal pochodzą z założeń, nie z pomiaru.
- **Cztery testy integracyjne są pomijane** — wymagają uprawnień
  administratora albo realnego obciążenia procesora.
- W magazynie certyfikatów użytkownika leży około czterdziestu porzuconych
  certyfikatów deweloperskich `CN=GameShift Development` z kolejnych buildów.
  Do posprzątania; żaden nie jest w Zaufanych głównych ani Zaufanych wydawcach.

# Plan wykonawczy: Dismode 0.1.1 — Safety Release

Status: ukończony  
Utworzono: 2026-07-30  
Ukończono: 2026-08-01  
Źródło: zaawansowany plan naprawy i rozwoju przekazany przez użytkownika

## Cel

Przestać automatycznie wykonywać albo sugerować działania, dla których
Dismode nie ma jeszcze dowodu poprawy frametime. Zachować istniejące,
transakcyjne mechanizmy procesu, ale zmienić domyślne zachowanie na
konserwatywne i wyjaśnialne.

## Audyt stanu przed zmianą

| Obszar | Stan produkcyjny | Decyzja 0.1.1 |
| --- | --- | --- |
| Priorytet gry | Nowe preferencje i UI domyślnie wybierają `AboveNormal`; szybki Play odczytuje zapisany `AboveNormal` albo `High`. | Domyślnie `Normal`; szybki Play nie podwyższa priorytetu z zapisanego profilu; `AboveNormal`/`High` wyłącznie ręczne, eksperymentalne. |
| Procesy tła | Analiza automatycznie zaznacza nowe procesy do `BelowNormal + EcoQoS` albo zamknięcia. | Pokazywać kandydatów, ale nie zaznaczać nowych automatycznie. Zachować tylko uprzednio zapisane, jawne reguły użytkownika. |
| Usługi | Istnieje adapter i testy na fake adapterze; brak instancji w SessionHost i brak RPC wykonującego stop. | Pozostawić jako kod eksperymentalny/read-only. Nie rekomendować zatrzymania tylko dlatego, że usługa jest opcjonalna. |
| Plan zasilania | Istnieje adapter i testy na fake adapterze; brak instancji w produkcji i brak RPC aktywacji. Klon planu nie zmienia parametrów. | Pozostawić wyłącznie eksperymentalnie; nie prezentować jako optymalizacji ani wzrostu FPS. |
| Realtime | Blokowany. | Bez zmian — pozostaje niedozwolony. |
| `High` | Rzeczywista, odwracalna akcja Windows; wcześniej mogła wejść z zapisanego profilu. | Zachować tylko po ręcznym wyborze i ostrzeżeniu; nigdy z domyślnej/szybkiej ścieżki. |

## Zakres tej iteracji

1. Ustawić `Normal` jako domyślny priorytet we wszystkich ścieżkach.
2. Odłączyć zapisany priorytet od szybkiego Play.
3. Wymagać świadomego potwierdzenia dla ręcznego `AboveNormal`/`High`.
4. Zmienić analizę tła na listę sugestii bez automatycznego zaznaczania.
5. Usunąć komunikaty sugerujące wzrost FPS z RAM oraz zatrzymywanie usług.
6. Dodać testy regresji dla braku automatycznego podwyższania priorytetu.

## Poza zakresem tej iteracji

- aktywacja produkcyjnych usług lub planów zasilania;
- nowe tweaki, CPU Sets, sterowanie sprzętem i AI;
- telemetria ETW, testy A/B i silnik rekomendacji — stanowią następne
  bramy po ukończeniu Safety Release;
- migracja produktu do nowej architektury modułów.

## Kryteria walidacji

- zapisany `High` nie może pojawić się w planie szybkiego Play;
- nowy profil i brak rekordu SQLite dają `Normal`;
- kandydaci tła bez zapisanej reguły pozostają niezaznaczeni;
- ręczne `AboveNormal`/`High` nadal mają pełny snapshot, verify i restore;
- adaptery usługi/zasilania pozostają poza produkcyjnym IPC;
- build, testy i format kończą się kodem 0.

## Zrealizowane zmiany

- `Normal` jest domyślnym priorytetem nowych preferencji, braku rekordu
  SQLite, klientów sesji i UI.
- Szybki Play nie odczytuje już zapisanego `AboveNormal` ani `High`.
  Zapisane reguły procesów tła nadal działają jako wcześniejsza, jawna
  decyzja użytkownika.
- Ręczne `AboveNormal` i `High` wymagają osobnego ostrzeżenia; `Realtime`
  pozostaje zablokowany.
- Analiza pokazuje kandydatów procesów tła bez automatycznego zaznaczania.
- EcoQoS jest opisane jako wskazówka dla planisty Windows, nie limit CPU.
  Working set zamykanych aplikacji nie jest przedstawiany jako wzrost FPS.
- Opcjonalne usługi mają zalecenie wyłącznie obserwacyjne. Produkcyjne IPC
  nadal nie tworzy akcji zatrzymania usług ani aktywacji planu zasilania.

## Dowody walidacji

- `dotnet build Dismode.sln --configuration Debug`: kod 0, 0 ostrzeżeń.
- `dotnet test Dismode.sln --configuration Debug --no-build`: 95/95
  (27 Unit, 12 Recovery, 53 Integration, 3 Security).
- `dotnet format Dismode.sln --verify-no-changes --no-restore`: kod 0.
- Test regresji zapisuje `High`, uruchamia ścieżkę szybkiego Play i
  potwierdza brak `BOOST_GAME_PRIORITY` przy zachowaniu jawnej reguły tła.
- Wyszukanie produkcyjnych punktów tworzenia
  `StopApprovedServiceAction` i `ActivateManagedPowerProfileAction` nie
  znalazło żadnego wywołania poza definicjami adapterów.
- Nie uruchamiano aplikacji, instalatora, usług ani gier.

# AGENTS.md

## Nadrzędna instrukcja pracy dla tej sesji

Wykonuj dokładnie zlecone zadanie. Używaj narzędzi i skilli tylko wtedy, gdy są niezbędne do jego wykonania lub sprawdzenia wyniku. Jeśli wystarcza kontekst rozmowy, odpowiadaj bez narzędzi. Nie uruchamiaj rutynowych inspekcji, dodatkowych audytów ani agentów. Wyszukuj konkretne symbole i czytaj potrzebne fragmenty zamiast całych dużych plików. Nie czytaj ponownie niezmienionych treści. Ograniczaj wyniki narzędzi i długość odpowiedzi. Zachowaj niezbędną weryfikację i kończ po wykonaniu zadania.

W razie sprzeczności ta instrukcja ma pierwszeństwo przed poniższymi zasadami projektu.

## Linia rozwoju — sprawdź, zanim cokolwiek zmienisz

Jedyną linią rozwoju jest gałąź `main`. Bieżąca wersja programu to `<Version>` w `Directory.Build.props`.

28.09.2026 sesja w chmurze sklonowała nieaktualny `main` (0.3.0, 127 commitów wstecz) i naprawiała dawno zmieniony kod. Żeby to się nie powtórzyło, na starcie każdej sesji:

1. `git fetch origin`, potem `git status -sb`.
2. Pracuj na `main` albo na gałęzi odbitej od najnowszego `origin/main`.
3. Jeśli `git rev-list --count HEAD..origin/main` jest większe od 0, najpierw zaktualizuj bazę; nie poprawiaj starszego kodu.
4. Porównaj `<Version>` z `Directory.Build.props` z tą na `origin/main` (`git show origin/main:Directory.Build.props`); jeśli Twoja jest niższa, przerwij i zgłoś to użytkownikowi.

Krótkie gałęzie zadaniowe odbijaj od `origin/main` i wracaj przez PR do `main`. Nie twórz długo żyjących gałęzi z numerem wersji w nazwie.

## Rola

Jesteś autonomicznym inżynierem pracującym bezpośrednio w tym repozytorium.

Zasada nadrzędna:

> Najpierw poznaj lokalny kontekst, następnie wykonaj najmniejszą kompletną zmianę, zweryfikuj ją i nie naruszaj pracy niezwiązanej z zadaniem.

## Kolejność pracy

Dla każdego nietrywialnego zadania stosuj:

Inspect → Locate → Patch → Verify → Review.

Nie zgaduj ścieżek, API, komend ani struktury, jeśli można je sprawdzić w repozytorium.

Przed edycją:
- sprawdź `git status --short`,
- odczytaj właściwe instrukcje `AGENTS.md`,
- zlokalizuj istniejącą implementację,
- sprawdź tylko pliki i zakresy wymagane do zadania.

## Minimalne edycje

Dla istniejących plików tekstowych domyślnie stosuj edycje liniowe lub hunki.

NIE przepisuj całego istniejącego pliku, jeśli zmianę można poprawnie wykonać przez lokalny patch.

Preferencja:
1. line edit — pojedyncza, jednoznaczna zmiana;
2. hunk patch — kilka powiązanych linii lub bloków;
3. full-file replacement — wyłącznie gdy plik jest nowy albo rzeczywiście wymaga prawie całkowitej rekonstrukcji.

Przed patchem ustal jednoznaczny anchor: symbol, funkcję, klasę lub unikalny fragment tekstu.

Patch musi zawierać minimalny wystarczający kontekst. Nie zmieniaj sąsiednich linii tylko dla formatowania.

Po patchu sprawdź `git diff -- <path>`.

Jeżeli diff obejmuje niezamierzone linie, cofnij własną zmianę i wykonaj węższy patch.

## Granice pełnego replacementu

Pełne zastąpienie istniejącego pliku jest dozwolone tylko gdy zachodzi co najmniej jeden warunek:

- treść pliku jest generowana przez oficjalny generator projektu;
- użytkownik jawnie zażądał pełnego rewrite;
- ponad połowa semantycznej zawartości musi zostać zmieniona i hunki byłyby mniej bezpieczne;
- format pliku wymaga kontrolowanej pełnej serializacji;
- plik jest tak mały, że cały plik stanowi pojedynczą jednostkę logiczną.

W pozostałych przypadkach użyj patcha.

Nie zastępuj lockfile, wygenerowanego kodu ani artefaktu builda ręcznie, jeśli istnieje jego generator.

## Higiena filesystemu

Nie twórz scratchpadów, plików tymczasowych ani backupów w root projektu.

Nie twórz nazw typu:
`temp.*`, `test2.*`, `utils_new.*`, `final_v2.*`, `*.bak`.

Pliki trwałe umieszczaj wyłącznie w strukturze zgodnej z repozytorium.

Pliki tymczasowe i backupy operacyjne przechowuj poza repozytorium albo w dedykowanym ignorowanym katalogu.

Używaj ścieżek względnych względem repozytorium w patchach.

Nigdy nie pozwalaj ścieżce patcha wyjść poza workspace przez `..`, symlink ani ścieżkę absolutną.

## Ochrona istniejącej pracy

Nie resetuj ani nie usuwaj zmian, których sam nie utworzyłeś.

Zabronione bez jawnego polecenia użytkownika:
`git reset --hard`
`git restore .`
`git checkout -- .`
`git clean -fd`
automatyczny stash cudzych zmian.

Jeżeli target ma pre-existing modifications, zachowaj je i edytuj tylko wymagane linie.

## Kontekst i cache

Nie czytaj wielokrotnie całego dużego pliku, jeśli jego zawartość się nie zmieniła.

Najpierw:
- wyszukaj symbole,
- odczytaj mały zakres,
- zapamiętaj hash pliku i przeczytane zakresy.

Cache jest ważny tylko dopóki hash źródła pozostaje identyczny.

Po każdej własnej edycji natychmiast unieważnij cache zmienionego pliku.

`AGENTS.md` oraz dokumentacja repo są źródłem trwałych reguł.
Efemeryczny cache nie jest źródłem prawdy.

## Ścieżki i platforma

Stosuj API ścieżek właściwe dla platformy, np. `Path` / `pathlib`, zamiast ręcznego łączenia separatorów.

Nie zakładaj `/` jako separatora systemowego w kodzie runtime.

Zachowuj istniejące:
- encoding,
- BOM,
- LF/CRLF,
- permissions,
- executable bit,
o ile zadanie nie wymaga ich zmiany.

Nie wykonuj line patch na pliku binarnym lub nierozpoznanym encodingu.

## Bezpieczeństwo

Nie odczytuj ani nie ujawniaj sekretów, jeśli zadanie tego nie wymaga.

Nie kopiuj do odpowiedzi wartości z:
`.env`,
tokenów,
API keys,
SSH keys,
credential stores.

Nie wyłączaj sandboxa, testów, walidacji, lintingu ani zabezpieczeń tylko po to, aby zadanie przeszło.

## Git

Git służy przede wszystkim do inspekcji i weryfikacji.

Przed edycją:
`git status --short`

Po edycji:
`git diff -- <changed-paths>`
`git diff --check`

Nie wykonuj commit, push, rebase, amend ani force-push bez jawnej instrukcji użytkownika lub reguły repozytorium.

## Weryfikacja

Po zmianie uruchom najmniejszy adekwatny zestaw:

1. test dotyczący zmienionej funkcjonalności;
2. odpowiedni typecheck;
3. lint/static analysis;
4. build, jeśli zmiana może wpływać na kompilację;
5. szersze testy tylko gdy są uzasadnione.

Nie twierdź, że test przeszedł, jeśli nie został uruchomiony.

Jeżeli test nie przechodzi:
- sprawdź rzeczywisty błąd,
- ustal czy regresja pochodzi z Twojej zmiany,
- popraw przyczynę,
- uruchom ponownie właściwy test.

## Definition of Done

Zadanie jest zakończone dopiero gdy:

- żądane zachowanie działa;
- patch jest minimalny;
- nie ma przypadkowych zmian;
- nie ma debug artifacts;
- importy/referencje są poprawne;
- adekwatna weryfikacja została wykonana;
- finalny diff został przejrzany.

## Format końcowego raportu

### Zmieniono
Krótko opisz implementację.

### Pliki
Wymień wyłącznie pliki rzeczywiście zmienione.

### Weryfikacja
Podaj faktycznie wykonane komendy i wynik.

### Uwagi
Podaj tylko realne ograniczenia lub blokery.

Nie wypisuj całych zmienionych plików, jeśli użytkownik tego nie zażądał.

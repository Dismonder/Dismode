# AGENTS.md

## Rola

Pracuj jako starszy inżynier full-stack Windows. Rozwijaj GameShift małymi, kontrolowanymi zmianami i utrzymuj zgodność z `docs/CODEX_CONTEXT.md`.

## Kolejność pracy

1. Przeczytaj ten plik.
2. Przeczytaj `docs/CODEX_CONTEXT.md`.
3. Przeczytaj aktywny plan z `docs/exec-plans/active/`.
4. Czytaj tylko pliki związane z bieżącym etapem.
5. Po znaczącej zmianie aktualizuj kontekst i plan.

## Inwarianty

- UI nie działa jako administrator i nie modyfikuje systemu bezpośrednio.
- Nie dodawaj arbitralnego wykonywania poleceń, PowerShella, plików wykonywalnych ani dowolnych zapisów rejestru.
- Każda przyszła mutacja systemu musi mieć snapshot, zapis intencji, weryfikację, idempotentne przywracanie i niezależny journal.
- Nie wykonuj operacji na produkcyjnych usługach ani aktywnym planie zasilania przed zaliczeniem bramy recovery na adapterach testowych.
- Hard Safety Policy ma pierwszeństwo przed regułami użytkownika.
- Nie wyłączaj zabezpieczeń Windows, sterowników, anti-cheat ani krytycznych usług.
- Nie obiecuj wzrostu FPS bez rzeczywistego pomiaru.

## Walidacja

Uruchamiaj najmniejszy sensowny zestaw:

```powershell
dotnet restore GameShift.sln
dotnet build GameShift.sln --configuration Debug
dotnet test GameShift.sln --configuration Debug --no-build
dotnet format GameShift.sln --verify-no-changes
```

Nie raportuj testu jako wykonanego, jeżeli nie zakończył się rzeczywistym wynikiem.


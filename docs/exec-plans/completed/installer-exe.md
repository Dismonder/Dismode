# Plan wykonawczy: instalator EXE Dismode

Status: ukończony — instalator zbudowany i zweryfikowany statycznie;
install/uninstall smoke pozostaje ręczną kontrolą po zamknięciu działającego
Dismode  
Utworzono: 2026-07-29

## Cel

Zbudować pojedynczy instalator EXE dla aktualnej architektury Dismode,
bez uzależniania komputera docelowego od osobnej instalacji .NET lub Windows
App Runtime.

## Zakres

- self-contained publikacja win-x64 istniejących czterech procesów;
- instalacja do `Program Files\Dismode`;
- skróty menu Start i opcjonalnie pulpitu;
- standardowy deinstalator Windows;
- ochrona przed aktualizacją podczas aktywnej sesji;
- manifest SHA-256 payloadu i osobna suma instalatora;
- zachowanie danych użytkownika i journalu w LocalAppData;
- brak instalowania SystemAgent jako usługi, dopóki jego produkcyjny model
  recovery nie przejdzie wymaganej walidacji VM.

## Etapy

- [x] Wybrać Inno Setup 6 i zainstalować kompilator w zakresie użytkownika.
- [x] Dodać metadane wersji 0.1.0.
- [x] Dodać opcjonalną publikację self-contained.
- [x] Dodać źródło i skrypt budowy instalatora.
- [x] Zbudować świeży payload oraz instalator.
- [x] Zweryfikować kompilację, manifesty, PE i zawartość instalatora.
- [x] Uzupełnić dokumentację i przenieść plan do ukończonych.

## Decyzje bezpieczeństwa

- Instalator wymaga UAC tylko dla zapisu do Program Files.
- Nie kończy automatycznie gry, SessionHost ani recovery.
- Aktualizacja i deinstalacja są blokowane, gdy działa komponent Dismode
  albo jego proces PresentMon.
- Program Files korzysta z dziedziczonych ACL Windows; instalator nie
  rozluźnia uprawnień.
- LocalAppData nie jest kasowane podczas deinstalacji.
- Instalator pozostaje niepodpisany, ponieważ repozytorium nie ma prywatnego
  certyfikatu Authenticode; dokumentacja mówi o tym jawnie.

## Walidacja

Oczekiwane dowody:

- `dotnet build` bez błędów;
- testy i format verification zgodne z `AGENTS.md`;
- wszystkie wymagane EXE, XBF, PRI, runtime oraz przypięty PresentMon w
  payloadzie;
- instalator ma nagłówek PE, prawidłową wersję i powtarzalną nazwę;
- SHA-256 instalatora zapisany obok EXE;
- instalacja, pierwsze uruchomienie i bezpieczna deinstalacja jako ręczny
  smoke po zamknięciu aktywnych komponentów użytkownika.

## Wynik

- Build Release: 0 ostrzeżeń i 0 błędów.
- Testy: 94/94.
- Format verification: kod 0.
- Payload: 706 plików, 256,56 MiB, wszystkie wpisy manifestu zgodne.
- Diagnostyka self-contained SystemAgent: kod 0, protokół 4.
- Instalator: 71 823 084 B, wersja `0.1.0.0`.
- SHA-256:
  `164C642355EE5F59F0CB9BB652D3EDDED1A73434AF371800816A1F556CA4F630`.
- Podpis: `NotSigned`, zgodnie z jawnym brakiem certyfikatu.
- Install/uninstall smoke nie został wykonany, ponieważ bieżący Dismode
  nadal działa, instalator poprawnie blokuje taki scenariusz, a użytkownik
  nie zezwolił na sterowanie komputerem ani zamykanie jego procesów.

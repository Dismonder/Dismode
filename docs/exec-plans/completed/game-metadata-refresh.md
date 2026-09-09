# Plan wykonawczy: lokalne metadane gier

Status: ukończony i zainstalowany w 0.1.7  
Utworzono: 2026-08-02

## Cel

Trwale przechowywać źródło i identyfikator gry, launcher, ostatnie
uruchomienie, łączny czas gry, lokalną grafikę hero oraz czas ostatniego
odświeżenia. Dane mają być odświeżane z lokalnych źródeł z kontrolą TTL,
bez sieci, poświadczeń i uruchamiania gier.

## Etapy

1. Dodać domenowy model i repozytorium metadanych niezależne od UI.
2. Dodać migrację SQLite zachowującą profile i istniejące ArtworkPath.
3. Zaimplementować lokalnych providerów Steam dla appmanifest,
   localconfig i librarycache oraz Epic dla ograniczonego odczytu
   `LastPlayedGame` z lokalnego `GameUserSettings.ini`.
4. Dodać koordynator odświeżania: TTL przy starcie oraz wymuszone,
   pojedyncze odświeżenie po zakończonej sesji.
5. Podłączyć koordynator do synchronizacji biblioteki i finalizacji sesji.
6. Dodać testy migracji, parsera/providerów, TTL i hooka sesji.
7. Uruchomić build, testy dotkniętych modułów i format.

## Inwarianty

- Brak połączeń sieciowych i odczytu poświadczeń Steam.
- Brak zapisu do plików Steam i brak uruchamiania gier.
- Błąd opcjonalnych metadanych nie może zablokować przywrócenia sesji.
- Istniejący profil oraz ArtworkPath pozostają źródłem kompatybilności.
- UI/XAML nie należy do zakresu.

## Walidacja

- migracja v7 → v8 zachowuje profil i grafikę;
- kontrolowany appmanifest/localconfig daje poprawny czas i LastPlayed;
- drugie odświeżenie przed TTL nie czyta providera ponownie;
- zakończenie sesji wymusza jedno odświeżenie profilu;
- pełna regresja nie uruchamia prawdziwych gier.

## Zrealizowane

- Dodano model i repozytorium metadanych oraz migrację SQLite v8.
- Steam odczytuje lokalnie appmanifest, localconfig i librarycache: datę
  ostatniego uruchomienia, czas gry, launcher i grafikę.
- Epic Games odczytuje wyłącznie lokalne wiersze `LastPlayedGame=` i
  dopasowuje drugi segment do `CatalogItemId`; nie czyta ani nie loguje pól
  konta lub poświadczeń.
- Odświeżanie ma TTL 6 godzin przy starcie i wymuszone odświeżenie po sesji.
- Świeże rekordy utworzone przed dodaniem providera są wzbogacane jeden raz,
  a później ponownie podlegają TTL.
- Na komputerze użytkownika potwierdzono kompletne grafiki dla 9/9 profili,
  rzeczywiste dane Steam dla 6 gier oraz daty Epic dla Dead by Daylight i
  Dying Light.

## Dowody

- Release build: 0 ostrzeżeń i 0 błędów.
- Pełna regresja: 128/128.
- Zainstalowana baza: schemat v8, metadane Steam/Epic zapisane lokalnie.

## Zrealizowane

- Provider Epic czyta strumieniowo wyłącznie rekordy `LastPlayedGame`,
  dopasowuje drugi segment identity do `CatalogItemId`, wybiera najnowszy
  czas i nie analizuje ani nie loguje pól poświadczeń.
- Odczyt Epic jest lokalny, ma limit 4 MiB i współdzielenie
  `ReadWrite/Delete`; launcher jest wykrywany tylko w standardowych
  lokalizacjach, a czas całkowity pozostaje jawnie niedostępny.
- Świeży rekord będący wyłącznie placeholderem profilu jest jednokrotnie
  wzbogacany po pojawieniu się providera; po uzyskaniu danych ponownie działa
  sześciogodzinny TTL.
- Build testów integracyjnych: 0 ostrzeżeń; testy Epic/TTL: 5/5; format
  dotkniętych plików: kod 0.

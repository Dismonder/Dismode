# Obsługa lokalnego wydania GameShift

## Uruchomienie

Kliknij skrót `GameShift` na pulpicie albo uruchom:

```text
artifacts\GameShift-App\GameShift.exe
```

Launcher działa bez podwyższonych uprawnień i prosi o zgodę UAC dla stałego
`GameShift.SessionHost.exe`. SessionHost wykonuje działania sesji oraz
uruchamia przypięty miernik klatek. SystemAgent i UI pozostają
niepodwyższone. Kolejne uruchomienie nie tworzy duplikatów i maksymalizuje
istniejące okno.

## Szybka diagnostyka

W aplikacji otwórz **Diagnostyka** i wybierz **Sprawdź ponownie**. Oba hosty
powinny odpowiadać przez protokół `v4`; SessionHost pokazuje `Ready`, a
SystemAgent `ReadOnly`.

Po zbudowaniu rozwiązania można wykonać również kontrolę obu pipe’ów:

```powershell
dotnet tools\GameShift.SessionSimulator\bin\Release\net10.0-windows10.0.26100.0\GameShift.SessionSimulator.dll --pipe both
```

Rozszerzony, nadal read-only test odrzucania błędnych żądań:

```powershell
dotnet tools\GameShift.SessionSimulator\bin\Release\net10.0-windows10.0.26100.0\GameShift.SessionSimulator.dll --pipe both --security-self-test
```

Integralność dołączonego PresentMon można sprawdzić bez uruchamiania gry:

```powershell
dotnet tools\GameShift.SessionSimulator\bin\Release\net10.0-windows10.0.26100.0\GameShift.SessionSimulator.dll --presentmon-status --presentmon-path artifacts\GameShift-App\Tools\PresentMon\PresentMon-2.5.1-x64.exe
```

Wynik `Ready` potwierdza dokładny rozmiar i SHA-256 przypiętego wydania 2.5.1.
Sam pomiar uruchamia się automatycznie dopiero dla PID aktywnej gry. Stan
oczekiwania na klatki nie jest zastępowany estymowanym FPS.

## Awaria UI lub SessionHost

1. Nie zamykaj procesu gry i nie usuwaj danych GameShift.
2. Ponownie kliknij skrót `GameShift`.
3. SessionHost odczyta niezależny journal i spróbuje odzyskać aktywną sesję
   na podstawie PID, czasu startu, ścieżki, SHA-256, SID i session ID.
4. Otwórz **Diagnostyka** i sprawdź oba połączenia.
5. Jeżeli przygotowany plan zniknął po awarii hosta, przygotuj go ponownie.
   UI celowo nie pozwala uruchomić planu, którego host nie może potwierdzić.

Pliki użytkownika:

```text
%LOCALAPPDATA%\GameShift\gameshift-user.db
%LOCALAPPDATA%\GameShift\user-recovery.jsonl
```

SQLite przechowuje profile i historię. Journal JSONL jest niezależnym źródłem
recovery i nie wolno go usuwać przed zakończeniem niedokończonej sesji.

## Aktualizacja lokalnego wydania

Nie podmieniaj plików w trakcie aktywnej sesji. Najpierw zakończ sesję w UI
i potwierdź w diagnostyce brak aktywnej sesji. Publikuj projekty w kolejności:
Launcher, SessionHost, SystemAgent, a UI jako ostatnie. UI musi dostarczyć
`App.xbf`, `MainWindow.xbf` i `GameShift.UI.pri`.

Zalecana komenda:

```powershell
.\tools\Build-LocalRelease.ps1 -CreateDesktopShortcut
```

Skrypt odmówi aktualizacji, jeżeli komponenty nadal działają albo journal
zawiera niedokończoną sesję. Przed podmianą wykonuje build, testy, kontrolę
formatu i walidację wymaganych plików.

Katalog `artifacts` może zawierać wersjonowane kopie
`GameShift-App.backup-*`. Cofnięcie lokalnej aktualizacji polega na:

1. zamknięciu UI oraz obu hostów;
2. zachowaniu bieżącego journalu i bazy użytkownika;
3. przywróceniu całego poprzedniego katalogu wydania, nie pojedynczych DLL;
4. ponownym uruchomieniu `GameShift.exe` i teście obu pipe’ów.

Nie mieszaj plików z dwóch buildów i nie cofaj bazy utworzonej przez nowszy
schemat.

## Instalator EXE

Samodzielny instalator dla Windows 11 x64 powstaje komendą:

```powershell
.\tools\Build-Installer.ps1
```

Wynik:

```text
artifacts\installer\GameShift-Setup-0.1.0-win-x64.exe
artifacts\installer\GameShift-Setup-0.1.0-win-x64.exe.sha256
```

Instalator zawiera .NET, Windows App Runtime, wszystkie cztery komponenty
GameShift, zasoby XBF/PRI i przypięty PresentMon. Instaluje do
`Program Files\GameShift`, tworzy skrót menu Start, opcjonalny skrót pulpitu
oraz standardowy wpis deinstalacji Windows.

Przed instalacją, aktualizacją lub deinstalacją zakończ aktywną sesję i zamknij
GameShift. Instalator blokuje operację, gdy wykryje dowolny komponent GameShift
albo jego PresentMon; celowo nie kończy ich automatycznie. Deinstalacja nie
usuwa `%LOCALAPPDATA%\GameShift`, ponieważ znajdują się tam profile, historia
i niezależny journal recovery.

Wydanie 0.1.0 nie ma certyfikatu Authenticode. Ostrzeżenie SmartScreen o
nieznanym wydawcy jest więc oczekiwane. Przed uruchomieniem można porównać
SHA-256:

```powershell
Get-FileHash `
  .\artifacts\installer\GameShift-Setup-0.1.0-win-x64.exe `
  -Algorithm SHA256
```

Oczekiwana suma bieżącego artefaktu:

```text
164C642355EE5F59F0CB9BB652D3EDDED1A73434AF371800816A1F556CA4F630
```

## Zakres bieżącego wydania

Klikalne wydanie wykonuje rzeczywiste, odwracalne działania w sesji użytkownika:

- wykrywa biblioteki Steam, Epic, GOG, Roblox oraz dostępne instalacje Xbox;
- dołącza do jednego już działającego procesu gry po zgodności ścieżki,
  SHA-256, SID i sesji zamiast uruchamiać duplikat;
- ustawia grę na `AboveNormal` lub, po jawnym wyborze, `High`;
- po ustawieniu priorytetu odczytuje go z procesu przez Windows; `High` jest
  rzeczywistą klasą widoczną również w Menedżerze zadań;
- ustawia wybrane opcjonalne procesy tła na `BelowNormal` i, po jawnym
  wyborze, włącza EcoQoS;
- łagodnie zamyka wybrane aplikacje i uruchamia je ponownie po sesji;
- łagodnie zamyka grę na żądanie;
- śledzi potomne procesy gry po zamknięciu launchera i zachowuje ich
  tożsamości w journalu na potrzeby restartu SessionHost;
- uruchamia przypięty PresentMon 2.5.1 dla śledzonych PID gry, odczytuje
  rzeczywiste zdarzenia ETW i oblicza FPS oraz czas klatki;
- zapisuje każdą intencję i stan oryginalny w journalu przed zmianą.

Procesy Windows, launchery, anti-cheat oraz pliki pomocnicze z katalogu
instalacyjnego wybranej gry nie są proponowane do zamknięcia, obniżenia
priorytetu ani EcoQoS.

PresentMon jest stałym komponentem w `Tools\PresentMon`. Skrypt wydania i
SessionHost sprawdzają rozmiar oraz SHA-256 przed uruchomieniem. Argumenty są
zamknięte: tylko zweryfikowane PID drzewa aktywnej gry, stdout CSV v2 oraz
unikalna nazwa sesji ETW. Proces miernika kończy się przy zakończeniu lub
recovery sesji. GameShift nie pobiera ani nie uruchamia dowolnego pliku
podanego przez użytkownika.

Wykrywanie Xbox przyjmuje tylko dostępny `MicrosoftGame.Config` z
bezpośrednio zweryfikowanym plikiem EXE. Chronione obrazy pakietów bez takiego
EXE są pomijane, ponieważ bieżący launcher nie udaje obsługi, której nie może
bezpiecznie wykonać.

Opcjonalne aplikacje tła są zamykane łagodnie, aby można je było bezpiecznie
uruchomić ponownie. Jawny czerwony przycisk zamknięcia gry może natychmiast
zakończyć tylko ponownie zweryfikowane procesy okna aktywnej gry; RPC nie
przyjmuje dowolnego PID. Produkcyjne zatrzymywanie usług i przełączanie planu
zasilania pozostają zablokowane. Priorytet `Realtime` również pozostaje
zablokowany: może zagłodzić krytyczne wątki systemu i nie stanowi wiarygodnej
metody zwiększania FPS.
Aktywacja wymaga kontrolowanej VM, własnej testowej usługi, crash injection
oraz potwierdzonego przywrócenia 100% kontrolowanych zmian. Ręczne usuwanie
journalu lub obchodzenie blokad Hard Safety nie jest procedurą naprawczą.

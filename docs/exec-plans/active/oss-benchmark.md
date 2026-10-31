<!-- Wynik workflow 2026-10-29/30: 19 projektow open source przeczytanych (Codex), porownanych z kodem Dismode (Codex), sprawdzonych przez sceptykow (Sonnet), uszeregowanych (Opus). -->
> **Stan wdrozenia (2026-10-31):** partia 1 (2026-10-30) plus #5, #7, #8, #9, #10 (PR #11/#12) i #11-#17 (#17 faza 1, PR #13) wdrozone w 0.9.0. #17 faza 2 zaimplementowana w galezi zadaniowej. Otwarte: #4B, #18 oraz decyzje z sekcji "Needs owner decision".

# Dismode: ranking propozycji z analizy projektów open source

Ranking jest ułożony według wartości dla gracza w stosunku do nakładu i ryzyka. Opieram się wyłącznie na przekazanym wejściu: pliki nie były otwierane, a numery linii pochodzą z werdyktów. Pewność rankingu jako całości: MEDIUM. Kolejność zmieniłyby pomiary PresentMon albo dowód, że któryś błąd w praktyce nie występuje.

Uwaga do stanu repo: na gałęzi `fix/bug-hunt-2026-10-08` są niezacommitowane zmiany w `MemoryUserRuntime.cs`, `ProBalanceEngine.cs` i `MemoryOptimizerEngine.cs`. Pozycje, które dotykają tych plików, warto zaczynać dopiero po zamknięciu tamtej pracy.

## Ranking

### 1. Monitor procesu gry ma działać dalej, gdy przechwytywanie FPS zawiedzie
- **Dlaczego:** to realny błąd. Wyjątek podczas sprzątania PresentMon kończy cicho zadanie `_monitorTask`. Dismode przestaje wtedy wykrywać wyjście z gry, a aplikacje w tle zostają ograniczone po jej zamknięciu.
- **Źródła:** Playnite (`GenericGameController.cs:731-760`, `ExtensionFactory.cs:652-689`). Błąd jest potwierdzony w kodzie Dismode: wywołania bez osłony w `LocalGameSessionOrchestrator.cs:1479-1507`, a jedyny catch jest w `:1839`.
- **Pliki:** `src/Dismode.Windows/Sessions/LocalGameSessionOrchestrator.cs` (`MonitorAsync`, blok `:1487-1512`, nowa flaga `FrameRateCaptureFaulted` w runtime).
- **Zakres:** łapać tylko `PresentMonCaptureCleanupException` oraz oczekiwane wyjątki I/O i procesu. `OperationCanceledException` przy anulowanym tokenie ma przechodzić dalej. `StopAsync` nie powtarzać w każdym ticku.
- **Test:** `GameSessionOrchestratorTests` z providerem, który rzuca w `SampleAsync` i w `StopAsync`, a potem z 3 pustymi obserwacjami. Asercje: sesja się kończy, tło jest przywrócone, błąd sprzątania nadal jest zgłoszony, a anulowanie kończy monitor.
- **Ryzyko:** niskie. **Nakład:** S–M. **Pewność:** HIGH.

### 2. Pełny stan EcoQoS (SystemManaged / On / Off) przy przywracaniu
- **Dlaczego:** dotyczy każdej sesji. Proces, którym przed sesją zarządzał Windows, zostaje po niej z jawnie wyłączonym throttlingiem, a niezwiązane bity maski są nadpisywane.
- **Źródła:** ThreadPilot (`ProcessMemoryPriorityService.cs:327,377`). Dismode: `ProcessPowerThrottlingController.cs:27-44`, `ProcessEcoQosState.cs:3`, `RuntimeProcessEcoQosAction.cs:136-194`.
- **Pliki:** `src/Dismode.Windows/Processes/ProcessPowerThrottlingController.cs`, `ProcessEcoQosState.cs`, `RuntimeProcessEcoQosAction.cs`, `ProcessEcoQosAction.cs`. Interop się nie zmienia, bo struktura ma już `ControlMask`.
- **Zakres:** enum `EcoQosMode`, `Set(mode)` jako read-modify-write tylko bitu ExecutionSpeed, porównanie przed zapisem, wersjonowanie rekordu journala.
- **Test:** round-trip wszystkich 3 trybów, zachowanie obcych bitów, zmiana zewnętrzna jako konflikt, ponowne użycie PID, rekord legacy (bool). Na procesie pomocniczym porównać natywne maski przed sesją i po niej, także po crashu.
- **Ryzyko:** niskie–średnie (migracja journala). **Nakład:** M. **Pewność:** HIGH.

### 3. Statystyki sesji z pojedynczych klatek: prawdziwa średnia, 1% / 0.1% low, hitche
- **Dlaczego:** dziś średnia jest liczona z okien jednosekundowych, a krótkie przycięcia znikają. Overlay liczy inny „1%” niż `BenchmarkAnalyzer`. Dla gracza to najbardziej widoczna poprawa pomiaru.
- **Źródła:** CapFrameX (`FrametimeStatisticProvider.cs:334,316,374,384`), OCAT (`PresentMon.cpp:950-960`). Dismode: `SessionFrameRateStatisticsAccumulator.cs:39-76`, `BenchmarkAnalyzer.cs:56-118`, `PerformanceOverlayWindow.xaml.cs:240-254`.
- **Pliki:**
  - nowy helper w `src/Dismode.Core/` (wydzielenie `Percentile`/`CalculateSlowestLow`)
  - `BenchmarkAnalyzer.cs`
  - `src/Dismode.Windows/Sessions/SessionFrameRateStatisticsAccumulator.cs`, `SessionFrameRateStatistics.cs`
  - `LocalGameSessionOrchestrator.cs:4028-4040` (punkt zasilania, niezweryfikowany)
  - `PresentMonFrameRateProvider.cs:722-730`
  - `src/Dismode.UI/ViewModels/HistoryListItem.cs`, `PerformanceOverlayWindow.xaml.cs`
  - `SqliteUserDataStore.cs` (dodatkowe kolumny, nullable)
- **Test:** syntetyczne tablice: stałe 16,67 ms daje 60/60/60, skok 200 ms daje oczekiwane lows i max, pusty strumień daje null, remisy progów. Do tego test zgodności z `BenchmarkAnalyzer`, round-trip JSON/SQLite starych i nowych rekordów oraz limit pamięci na długiej sesji (histogram albo ring).
- **Ryzyko:** niskie, bo to tylko odczyt. **Nakład:** M. **Pewność:** HIGH.

### 4. Poprawna diagnostyka nagrywania w tle oraz odwracalny zapis Game DVR
- **Stan (2026-10-31):** zrobione — część B: ręczny opt-in Experimental w System Optimizer, wyłącznie `HistoricalCaptureEnabled=0` w `HKEY_USERS\<SID>\Software\Microsoft\Windows\CurrentVersion\GameDVR` uwierzytelnionego użytkownika (ta sama ścieżka co diagnostyka; bez zmian `System\GameConfigStore`). Opis ostrzega o utracie powtórek. Istniejąca bramka `BenchmarkDecisionEngine` / `KeepGloballyAsync` wymaga wygranej A/B i jawnego utrwalenia; brak automatycznego presetu. Snapshot obecności, typu i wartości, durable journal przed zapisem, restore przez usunięcie pierwotnie nieobecnej wartości, crash replay i zachowanie zmian zewnętrznych; polityka `AllowGameDVR=0`, niejednoznaczna polityka oraz błędy dostępu blokują zapis. Bez migracji. Testy syntetyczne, bez zapisu do rzeczywistego rejestru, bez weryfikacji na sprzęcie i bez pomiaru FPS lub opóźnień.
- **Dlaczego:** obecna diagnostyka myli dostępność przechwytywania (`AppCaptureEnabled`) z nagrywaniem w tle (`HistoricalCaptureEnabled`) i wprowadza użytkownika w błąd. Zapis tej wartości jest prostą poprawką, ale przez kompromis trafia do decyzji właściciela.
- **Źródła:** Optimizer/hellzerg (`OptimizeHelper.cs:1214-1233`) i winutil (`tweaks.json:1883`) jako przykłady, czego nie robić (brak backupu). Dismode: `WindowsGamingEnvironmentDiagnostic.cs:77-114`, `SystemTweakCatalog.cs:231-250`.
- **Pliki, część A (diagnostyka):** `src/Dismode.Windows/Platform/WindowsGamingEnvironmentDiagnostic.cs`, tekst w `src/Dismode.UI/MainWindow.xaml.cs:7121`.
- **Pliki, część B (akcja):** nowy `GameCaptureRegistryAction` (na wzór `HibernateConfigurationAction`), `src/Dismode.Core/Actions/OptimizationActionKind.cs`, `src/Dismode.SystemAgent/SystemOptimization/WindowsSystemTweakRuntime.cs`, `SystemTweakCatalog.cs`.
- **Test A:** fałszywy rejestr (włączone, wyłączone, brak wartości, polityka `AllowGameDVR`, odmowa dostępu) z oczekiwanym stanem enabled/disabled/unknown.
- **Test B:** snapshot obecności i typu, przywrócenie braku wartości, crash recovery, konflikt ze zmianą zewnętrzną.
- **Ryzyko:** A niskie; B średnie (gałąź rejestru SID użytkownika, bo usługa działa jako SYSTEM). **Nakład:** A S, B M. **Pewność:** HIGH.

### 5. CPU Sets: trójstanowy odczyt, weryfikacja po zapisie, warunkowe przywracanie (wersja okrojona)
- **Dlaczego:** dziś Dismode nadpisuje ustawienie launchera lub użytkownika, a potem je czyści do pustego. To błąd poprawności; nie ma dowodu, że wpływa na FPS.
- **Źródła:** ThreadPilot (`ProcessCpuSetHandler.cs:259,352,397`, `GameSessionService.cs:1004`) i ProcGovernor (`apply.rs:341-428`; braku przywracania z ProcGovernor nie przejmujemy). Dismode: `ProcessCpuSets.cs:59-113`, `LocalGameSessionOrchestrator.cs:2227-2229,2391,2951`, `JournaledProBalanceActuator.cs:473-481,598-600,675-701`.
- **Pliki:** `src/Dismode.Windows/Cpu/ProcessCpuSets.cs`, `LocalGameSessionOrchestrator.cs`, `JournaledProBalanceActuator.cs` (oryginalne ID w istniejącym rekordzie zamiast samej flagi `steeredCpuSets`).
- **Zakres:** bez nowego `ProcessCpuSetsAction` i bez ThreeWayReconciler.
- **Test:** oryginał pusty lub niepusty, cel równy oryginałowi (no-op), błąd zapytania, niezgodny odczyt po zapisie, zmiana zewnętrzna, ponowne użycie PID, przywrócenie tylko gdy bieżące ID = zastosowane ID (porównanie jako zbiory).
- **Ryzyko:** średnie (wiele wywołań, schemat journala ProBalance). **Nakład:** M. **Pewność:** MEDIUM–HIGH.

### 6. Cooldown automatycznego czyszczenia pamięci także po czyszczeniu ręcznym
- **Dlaczego:** automatyczne czyszczenie potrafi ruszyć kilka sekund po ręcznym. Zysk jest mały, ale zmiana prawie darmowa.
- **Źródła:** Mem Reduct (`main.c:475-476,680-684`). Dismode: `MemoryUserRuntime.cs:46-53,145-150,196-216`, `AutoOptimizationPolicy.cs:17-90`.
- **Pliki:** `components/Dismode.MemoryOptimizer/src/Dismode.MemoryService/MemoryUserRuntime.cs` (helper `RecordIfFinished`) i `AutoOptimizationPolicy.cs` (lock).
- **Test:** ręczne czyszczenie przy spełnionych warunkach auto daje 0 przebiegów przed upływem cooldownu i 1 po nim. Failed i Partial włączają cooldown, Blocked i AlreadyRunning nie. Do tego wyścig `RecordCompleted` z `Evaluate` oraz przesunięcie harmonogramu.
- **Ryzyko:** niskie. **Nakład:** S. **Pewność:** HIGH.
- **Uwaga:** ten plik ma teraz niezacommitowane zmiany na gałęzi.

### 7. Utrata zdarzeń ETW ma unieważniać wynik benchmarku
- **Zrobione:** `CaptureLoss` (None / Confirmed z osobnymi licznikami zdarzeń i buforów / Unknown) przechodzi z PresentMon przez collector, gRPC, analizator i zapis JSON. Confirmed daje `IsValid=false` oraz blokuje decyzję o poprawie; Unknown tylko ostrzega. Późniejsze czyste linie i restart czytnika nie kasują straty w tym samym benchmarku. Sesyjne FPS nadal działają; diagnostyka trafia do logu.
- **Potwierdzone w przypiętej binarce 2.5.1:** SHA-256 `9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191`; napisy UTF-16LE `warning: %lu ETW buffers were lost.\n` (offset `0xc4e80`) i `warning: %lu ETW events were lost.\n` (`0xc4ed0`). Pole CSV `Dropped` nie oznacza utraty ETW. Oba strumienie tekstowe obsługują potwierdzone formaty.
- **Okno pomiaru:** collector zaczyna bez strat z ładowania, stabilizacji i poprzedniego celu; późniejsze komunikaty aktualizują go niezależnie od sesyjnego licznika. Koniec: zamrożenie klatek → do 500 ms na EOF po wyjściu procesu → anulowanie czytników → snapshot (z ostrzeżeniami końcowymi). Otwarty odziedziczony potok nie powoduje błędu zamknięcia. Nieprawidłowa próba zostaje w historii; czysty retry dostarcza nową próbę do porównania.
- **Zgodność:** nullable `CaptureLoss` w istniejących `CaptureJson`/`MetricsJson`, bez migracji tabel; stare rekordy bez pola zachowują wynik. Schemat danych użytkownika 18 i oddzielny schemat SystemOptimizer 1 bez zmian. gRPC: addytywne pole 13, uwzględnione w hashu idempotencji.
- **Niezweryfikowane:** nie wywoływano rzeczywistej utraty ETW ani przepełnienia. Nie potwierdzono, na którym strumieniu binarka wypisuje te formaty; obsługiwane są stdout i stderr. Komentarz o ~24 000 nie jest surowym logiem. `warning: ETW buffer overflow while processing events.` jest syntetycznym fixture Unknown, nie potwierdzonym tekstem 2.5.1; alternatywne komunikaty „dropped/processing” nie są uznawane za Confirmed. Przy powtórzeniach przechowywane jest maksimum zgłoszonego licznika osobno dla każdej jednostki; interpretacja kolejnych raportów jako sumy/przyrostów nie jest potwierdzona.
- **Nowa treść UI (do akceptacji właściciela):** „Utracono zdarzenia lub bufory ETW. Powtórz pomiar.” oraz „Ostrzeżenie: nierozpoznany komunikat PresentMon.”
- **Dlaczego:** utrata zdarzeń jest w repo udokumentowana (`LiveGameFrameTimeTests.cs:634` mówi o ~24 000 utraconych zdarzeń). Niepełny pomiar może dziś „udowodnić”, że optymalizacja pomogła.
- **Źródła audytu (przed zmianą):** OCAT (`PresentMon.cpp:1138-1148`, tekst z wersji 1.x, więc nie jest dowodem na 2.5.1). Dismode: `PresentMonFrameRateProvider.cs:689-701,826-828`, `BenchmarkAnalyzer.cs:27-32,70`.
- **Pliki:** `PresentMonFrameRateProvider.cs`, `PresentMonCaptureLossParser.cs`, `BenchmarkFrameCollector.cs` / `PresentMonBenchmarkCapture`, kontrakty i gRPC, `BenchmarkAnalyzer.cs`, `BenchmarkDecisionEngine.cs`, klient SystemOptimizer oraz koordynator SystemAgent; persystencja przez istniejący `SqliteSystemOptimizerStore`.
- **Test:** potwierdzone formaty binarki z podstawionymi licznikami (nie surowy log): czysty, strata, Unknown, strata + czyste linie, stdout, ostrzeżenie końcowe/EOF, zamrożenie klatek, blokada wygranej, czysty retry, gRPC/JSON i starszy JSON bez flagi.
- **Ryzyko:** nierozpoznane ostrzeżenie daje tylko Unknown zgodnie z decyzją właściciela; brak wymuszonej utraty na żywym ETW.

### 8. Wyświetlone FPS i zgubione klatki, nie tylko FPS prezentacji (opt-in)
- **Dlaczego:** przy tearingu, kompozycji okienkowej albo frame generation FPS prezentacji może zawyżać to, co faktycznie widać na ekranie.
- **Źródła:** PresentMon Intel (`MetricsCalculatorDisplay.cpp:27,149`), OCAT (`Recording.cpp:624-634`). Dismode: `PresentMonFrameRateProvider.cs:547` (`--no_track_display`), `PresentMonCsvParser.cs:125-130`.
- **Pliki:** `PresentMonFrameRateProvider.cs`, `PresentMonCsvParser.cs`, akumulator sesji, `PerformanceOverlayWindow.xaml.cs`.
- **Test:** fixture'y CSV (wszystkie klatki wyświetlone, część zgubiona, seria zgubień na końcu, brak kolumny z fallbackiem). Low liczone tylko z wyświetlonych klatek, a stare wyniki bez zmian.
- **Ryzyko:** średnie (narzut ETW, nazwy kolumn w 2.5.1). **Nakład:** M. **Pewność:** MEDIUM.

### 9. Ekran i system nie usypiają w trakcie sesji (PowerRequest)
- **Dlaczego:** gry obsługiwane samym padem i długie cutscenki mogą wygasić ekran. Część gier sama już zgłasza takie żądanie.
- **Źródła:** Feral GameMode (`gamemode-dbus.c:741,802`; dowód z Linuksa). Dismode: brak tej funkcji, plan `docs/exec-plans/active/cpu-module.md:145-146` (item 16).
- **Pliki:** nowy `IExecutionStateInhibitor` w `src/Dismode.Windows/Power/`, `PowerNativeMethods.cs`, właściciel w procesie sesji (`LocalGameSessionOrchestrator.cs`), ustawienie. Połączyć z item 16.
- **Test:** fałszywy inhibitor (apply, compensate, idempotentne zamknięcie, `DecideRecovery` jako no-op). Ręcznie: `powercfg /requests` w trakcie sesji, po niej i po kill usługi.
- **Ryzyko:** niskie. **Nakład:** S. **Pewność:** MEDIUM (wartość zależy od gry).

### 10. Rozdzielenie „gra na rdzeniach P” od ProBalance, per gra
- **Dlaczego:** gracz może zostawić ograniczanie tła, a wyłączyć przypinanie gry tam, gdzie szkodzi.
- **Źródła:** GameShift (`GameProfile.cs:398-420`, `OptimizationEngine.cs:142`). Dismode: `LocalGameSessionOrchestrator.cs:607-611,704`, `GameOptimizationPreferences.cs` (brak pola), `MainWindow.xaml.cs:2931,3128,4692,4815`.
- **Pliki:** `GameOptimizationPreferences.cs`, `SqliteUserDataStore.cs`, `LocalGameSessionOrchestrator.cs`, `MainWindow.xaml.cs`.
- **Test:** 4 kombinacje przełączników; przy wyłączonym przypinaniu 0 zapisów do procesu gry; migracja z domyślnym „włączone”.
- **Ryzyko:** niskie. **Nakład:** M (głównie UI). **Pewność:** MEDIUM. Nie zależy od pozycji 5.

### 11. Bez automatycznego wyboru CCD na podstawie liczby rdzeni (X3D)
- **Stan (2026-10-31):** zrobione — rzeczywiste maski i rozmiary L3, pakiety oraz NUMA; wybór L3 ≥1,5×, odmowa przy równych/niepełnych danych wielu grup L3 i wielu pakietach/węzłach NUMA. Pojedyncze L3 omija wybór CCD także wtedy, gdy LP E są poza nim (Meteor Lake); pełne pokrycie wymagane przy ≥2 L3. Zachowano politykę jednego CCD i Intel P/E oraz istniejące journal/restore/crash replay. Testy syntetyczne topologii i buforów Win32; bez weryfikacji na rzeczywistych X3D/CCD i Meteor Lake, wpływ na FPS i opóźnienia niezmierzony.
- **Dlaczego:** na 7950X3D i podobnych procesorach Dismode wybiera CCD0 według indeksu, a nie według L3.
- **Źródła:** ThreadPilot (`GameBoostPolicy.cs:44,54,79,158`). Dismode: `CpuTopology.cs:62-69`, `CpuAffinityPolicy.cs:170,188-190,276-307`, `SystemCpuTopologyProvider.cs:85-134`.
- **Pliki:** `SystemCpuTopologyProvider.cs` (`GetLogicalProcessorInformationEx`: RelationCache i RelationProcessorPackage, do tego `NumaNodeIndex`), `CpuTopology.cs`, `CpuAffinityPolicy.cs` (nowy powód odmowy `AmbiguousCacheGroups`).
- **Test:** syntetyczne topologie: równe L3 daje odmowę, asymetryczne L3 (≥1,5–2×) wybiera większe, wiele pakietów albo niepełne dane daje odmowę.
- **Ryzyko:** niskie, bo polityka robi się ostrożniejsza. **Nakład:** M. **Pewność:** MEDIUM. Bez pomiaru na sprzęcie nie twierdzimy, że daje FPS.

### 12. Obciążenie pojedynczych procesorów logicznych (diagnostyka)
- **Stan (2026-10-31):** zrobione — `LogicalProcessorLoadSampler` (NtQuerySystemInformation, jedna grupa) pokazuje CPU i maksimum procesora logicznego; wspolne obliczanie delt usuwa duplikacje w dotychczasowych samplerach. Probkowanie tylko przy widocznej nakladce, 1 Hz, bufory wielokrotnego uzytku; syntetyczne testy idle, regresji, krotkiego okna i wielu grup. Perf: 64 procesory, 100 000 iteracji, 0 B/wywolanie i B/s, ok. 0,0046 ms/wywolanie i ms/s przy 1 Hz (limit 8 B dla wyniku zerowego). Nie sprawdzono obciazenia jednego rdzenia na prawdziwym sprzecie ani wplywu na FPS.
- **Dlaczego:** jeden nasycony rdzeń ginie w średnim obciążeniu CPU.
- **Źródła:** LibreHardwareMonitor (`CpuLoad.cs:52,167`). Dismode: `SystemCpuLoadSampler.cs:13,21`, duplikat w `LocalSystemMetricsSampler.cs:25,113`, P/Invoke w `CpuProcessSampler.cs:36,118`.
- **Pliki:** nowy `src/Dismode.Windows/Cpu/LogicalProcessorLoadSampler.cs` (`NtQuerySystemInformation(SystemProcessorPerformanceInformation)`, jedna grupa procesorów), `LocalSystemMetricsSampler.cs`, `PerformanceOverlayWindow.xaml.cs`.
- **Test:** syntetyczne delty, uwzględnienie tego, że czas jądra zawiera idle, regresja licznika, za krótkie okno, null przy wielu grupach. Ręcznie: obciążenie jednego rdzenia.
- **Ryzyko:** niskie. **Nakład:** M. **Pewność:** MEDIUM.

### 13. Przejęcie gry po przekazaniu przez launcher lub DRM
**Stan (2026-10-31):** zrobione — okno 30 s, migawka instancji przy aktywacji, nastepca urodzony po starcie korzenia (takze przed jego wyjsciem), tozsamosc korzenia lub odrebnego EXE profilu z wlasnym hashem. Journal zamiaru przed przejeciem, osobny wynik odmowy i natychmiastowe przywracanie. Testy syntetyczne; bez weryfikacji z prawdziwym launcherem/DRM.
- **Dlaczego:** gdy DRM uruchomi grę ponownie poza obserwowanym drzewem procesów, Dismode przedwcześnie przywraca ustawienia.
- **Źródła:** Playnite (`GenericGameController.cs:696-728`, `ProcessMonitor.cs:119-164`). Dismode: `LocalGameSessionOrchestrator.cs:1539-1581`, `GameProcessTreeSessionTracker.cs:74-96`, `ManualGameProfileLauncher.cs:194-223`.
- **Pliki:** `LocalGameSessionOrchestrator.cs` (stan „czekam na przekazanie” przed GameExited), `ManualGameProfileLauncher.cs`, tracker.
- **Test:** proces znika, a po ponad 3 interwałach pojawia się zgodny zastępca. Do tego: timeout, anulowanie, dwóch kandydatów (odrzucenie), zły hash, użytkownik albo sesja, ponowne użycie PID, journal przed przejęciem.
- **Ryzyko:** średnie. **Nakład:** M. **Pewność:** MEDIUM.

### 14. Przełączenie częstotliwości odświeżania na czas sesji
**Stan (2026-10-31):** zrobione — opt-in (migracja 21), najwyzsze Hz przy biezacej rozdzielczosci i glebi koloru dopiero po pojawieniu sie widocznego okna drzewa gry; male i niewidoczne okna sa pomijane. Bez okna nie ma zmiany; monitor glowny tylko przy nieudanym mapowaniu istniejacego okna. Journal i recovery w SessionHost; oryginalny tryb juz przywrocony przez gre to przywrocenie bez konfliktu. Testy z falszywym API; bez przelaczania prawdziwego monitora.
- **Dlaczego:** pomaga, gdy monitor 144/240 Hz został na 60 Hz w grze bez trybu wyłącznego. Przypadek węższy, niż deklaruje propozycja.
- **Źródła:** AutoActions (`DisplayManagerBase.cs:293-312`; jako antywzorzec, bo zapisuje trwale i nie przywraca). Dismode nie ma tej funkcji.
- **Pliki:** nowy `Display/DisplayRefreshRateAction.cs`, `OptimizationActionKind.cs`, `AppendOnlyRecoveryJournal`, `SystemOptimizerRecoveryHostedService.cs`, wykonanie w sesji użytkownika (`DesktopUserProcessStarter` albo UI).
- **Test:** fałszywe API ekranu: wybór max Hz, no-op przy maksimum, właściwy monitor, journal i przywrócenie, crash replay, pominięcie przywrócenia po zmianie przez użytkownika.
- **Ryzyko:** średnie (mignięcie obrazu, wiele monitorów). **Nakład:** M. **Pewność:** MEDIUM.

### 15. Rzeczywisty czas gry bez uśpienia i czasu przygotowania
**Stan (2026-10-31):** zrobione — czas aktywnego systemu do ostatniej obserwacji zywego drzewa, bez opoznienia wykrycia wyjscia, oczekiwania na przekazanie i przywracania. Checkpoint co 15 s, nullable DurationSeconds (migracja 20), stare rekordy bez przeliczenia; bez mieszania z czasem dostawcy. Usunieto nieuzywana detekcje skokow UTC; czas pochodzi z zegara bez uspienia. Testy deterministyczne; bez fizycznego uspienia komputera.
- **Dlaczego:** sen systemu, korekty zegara i zaokrąglanie w górę każdej sesji zawyżają czas gry.
- **Źródła:** Playnite (`GenericGameController.cs:686-693,748-772`). Dismode: `GameMetadataRefreshService.cs:219-221,265-275`, `HistoryListItem.cs:15-16`, `MainWindow.xaml.cs:6099`, `SqliteUserDataStore.cs:1288-1289`.
- **Pliki:** te same oraz `LocalGameSessionOrchestrator.cs` i migracja (`DurationSeconds` nullable).
- **Test:** deterministyczny zegar: uśpienie i wybudzenie, skoki UTC, wolne przywracanie, wiele sesji poniżej minuty, checkpoint po crashu, odczyt starych rekordów.
- **Ryzyko:** niskie. **Nakład:** L. **Pewność:** MEDIUM.

### 16. Zatwierdzone reguły tła dla procesów uruchomionych w trakcie gry
**Stan (2026-10-31):** zrobione — opt-in (migracja 19), pelne rozpoznawanie tylko po pojawieniu sie nowych kluczy PID + czas startu w taniej migawce uzytej tez przez tracker. Granica czasu to rozpoznanie planu; pierwszy plan jest zapamietywany jako proba bez przyszlego ograniczania. Blad wylacza tylko reguly tla i jest logowany. Journal, recovery i wzajemne wykluczanie z ProBalance objete testami. Pomiar Perf: 300 procesow, 1 Hz, budzet 64 B/tick bez nowych procesow; bez pomiaru zysku FPS na grze.
**Perf (2026-10-31, dane sztuczne):** przed 70 176 B i 0,452536 ms/tick; po 0 B i 0,034387 ms/tick. Pomiar nie obejmuje zysku w grze ani rzeczywistego przechwytywania inwentarza Windows.
- **Dlaczego:** aplikacja, która zrestartuje się w trakcie gry, nie dostaje ograniczeń. Zysk niepotwierdzony pomiarem.
- **Źródła:** System Informer (`UserNotes/main.c:2599-2688`). Dismode: `LocalGameSessionOrchestrator.cs:299,335`, `SavedBackgroundRuleResolver.cs:16`.
- **Pliki:** pętla monitora w `LocalGameSessionOrchestrator.cs` (bez osobnego watchera), `SavedBackgroundRuleResolver.cs`, `JournaledProBalanceActuator.cs`.
- **Test:** proces pasujący do reguły startuje po aktywacji. Oczekiwane: dokładnie jedno zastosowanie w journalu na tożsamość procesu, przywrócenie, pominięcie procesu na pierwszym planie i drzewa gry, brak ponownego zastosowania po zmianie zewnętrznej.
- **Ryzyko:** średnie. **Nakład:** M. **Pewność:** LOW–MEDIUM.

### 17. Telemetria GPU, faza 1: tylko API systemu
- **Stan (2026-10-31):** zrobione — faza 2: NVML i ADL2 C API tylko w procesie UI, opcja domyslnie OFF (migracja 22), podpis Authenticode i dokladna nazwa wydawcy NVIDIA/AMD przed LoadLibraryEx z pelnej sciezki System32. Temperatura, zegar, moc calej karty i wentylator sa pokazywane tylko po udanym odczycie i jednoznacznym mapowaniu PCI adaptera PDH; brak mapowania lub adapter polaczony daje null. Worker 1 Hz tylko przy widocznej nakladce, shutdown/unload po ukryciu lub wylaczeniu; awaria biblioteki wylacza zrodlo bez ponawiania. Testy z atrapami obejmuja brak DLL, podpis, inicjalizacje, brak czujnika, usuniecie i wiele GPU oraz wylaczenie podczas odczytu. Perf z atrapami: 0 B/probke; pelna sesja CPU + PDH + czujniki ok. 0,111 ms/probke przy 1 Hz (bez kosztu prawdziwego API producenta). Wlasny test Live na RX 9070 XT: ADL odczytal 31 C, 15 MHz, 20 W BOARD_POWER i 0% wentylatora; NVML nieobecny, poprawnie niedostepny. Nie sprawdzono NVIDIA na sprzecie, wyboru adaptera podczas gry, wygladu nakladki w grze ani wplywu na FPS/latencje; nie deklarujemy takiego zysku.
- **Stan (2026-10-31):** zrobione — faza 1 przez PDH `GPU Engine`, `GPU Adapter Memory` i `GPU Process Memory`: systemowe liczniki WDDM bez elevacji i DLL producenta; angielskie sciezki dzialaja niezaleznie od jezyka Windows. Jedno zapytanie, bufory wielokrotnego uzytku, 1 Hz tylko przy widocznej nakladce; po ukryciu zapytanie jest zamykane. Nakladka pokazuje 3D i dedykowana pamiec adaptera gry (jednoznaczna aktywnosc/pamiec PID, inaczej najwyzsze 3D; remis daje null). Testy syntetyczne obejmuja brak API, reset/wrap, usuniecie adaptera, wiele GPU i stabilnosc uchwytow. Perf: 128 instancji silnikow + 2 adaptery + 64 instancje pamieci procesow, 10 000 iteracji, 0 B/wywolanie i B/s, ok. 0,161 ms/wywolanie i ms/s przy 1 Hz (limit 8 B). Replay obejmuje parser i obliczenia, bez kosztu rzeczywistego API; nie sprawdzono GPU, dostepu zwyklego konta ani rozkladow PresentMon na sprzecie i nie deklarujemy zysku FPS/latencji.
- **Dlaczego:** pozwala zestawić skoki czasu klatki z obciążeniem silników GPU i zajętością VRAM.
- **Źródła:** LibreHardwareMonitor (`D3DDisplayDevice.cs:175-247`). Dismode nie ma telemetrii GPU (tylko priorytet w `GpuPriorityNativeMethods.cs:35,40`).
- **Pliki:** nowy `GpuTelemetrySampler` w `src/Dismode.Windows/` (D3DKMTQueryStatistics albo PDH „GPU Engine”), `PerformanceOverlayWindow.xaml.cs`.
- **Test:** API nieobsługiwane, reset liczników, usunięcie adaptera, stabilność uchwytów, rozkłady PresentMon z telemetrią i bez.
- **Ryzyko:** niskie w fazie 1. **Nakład:** M (faza 1), L (całość). **Pewność:** MEDIUM.

### 18. Pomijanie zapisu priorytetu, gdy jest już taki sam
- **Dlaczego:** usuwa jedno wywołanie SetPriorityClass i około 3 zdarzeń journala na proces. Korzyść czysto księgowa, dla gracza znikoma.
- **Źródła:** System Informer (`main.c:2623-2688`). Dismode: `RuntimeProcessPriorityAction.cs:82-127`, `TransactionCoordinator.cs:61-191`.
- **Pliki:** `TransactionCoordinator.cs` (nowy status `Unchanged`), `RuntimeProcessPriorityAction.cs`, wywołania w `JournaledProBalanceActuator.cs:238` i `LocalGameSessionOrchestrator.cs:2266,2473,2714,3385`.
- **Test:** 0 wywołań setterów przy zgodnym stanie, Normal→BelowNormal nadal w journalu, zmiana zewnętrzna między prepare a validate daje Blocked.
- **Ryzyko:** średnie (status czytany jako porażka w wywołaniach). **Nakład:** S. **Pewność:** HIGH, że wartość jest niska.

## Recommended first batch

Pozycje niezależne, o niskim ryzyku, weryfikowalne testami jednostkowymi na tej maszynie:

1. **#1 Monitor odporny na awarię przechwytywania.** Prawdziwy błąd, który zostawia ograniczone tło po zamknięciu gry.
2. **#2 Pełny stan EcoQoS.** Błąd przy przywracaniu w każdej sesji. Domyślne mapowanie starych rekordów trzeba potwierdzić (zob. niżej).
3. **#3 Statystyki z pojedynczych klatek.** Tylko odczyt i migracja przez dodanie kolumn.
4. **#4A Poprawna diagnostyka Game DVR.** Tylko odczyt rejestru, bez zapisu.
5. **#6 Cooldown po ręcznym czyszczeniu pamięci.** Po zamknięciu bieżącej pracy w `MemoryUserRuntime.cs`.

Pozycje #1 i #3 zmieniają ten sam plik (`LocalGameSessionOrchestrator.cs`), ale w różnych miejscach (`:1487` i `:4028`). Najlepiej robić je kolejno albo w dwóch worktree z rebase. Z tej partii wyłączam #5 (CPU Sets): zmienia schemat journala ProBalance i to samo miejsce w orkiestratorze. Powinna być drugą partią razem z #7 (wymaga zebrania fixture'ów stderr PresentMon 2.5.1, czyli prawdziwego przebiegu, nie samych testów).

## Needs owner decision

- **#4B Zapis `HistoricalCaptureEnabled=0` — decyzja 2026-10-31:** ręczny opt-in, informacja o utracie powtórek, istniejąca bramka A/B przed jawnym utrwaleniem globalnym. Zaimplementowane w punkcie 4; bez domyślnego stosowania.
- **#2 Stare rekordy EcoQoS.** Proponuję: stary `false` traktować jako SystemManaged, stary `true` jako On. Akceptacja?
- **#3 Definicja hitcha.** Zostać przy definicji z `BenchmarkAnalyzer` (> max(50 ms, 3× mediana)) czy przejść na udział przycięć z CapFrameX (2,5× ruchomej linii bazowej)? Rekomendacja: jedna definicja, z BenchmarkAnalyzer.
- **#5 Zakres CPU Sets.** Wersja okrojona (rekomendowana) czy pełna integracja z TransactionCoordinator i ThreeWayReconciler? Czy niepuste, obce przypisanie CPU Sets ma domyślnie oznaczać pominięcie?
- **#8 Śledzenie wyświetlania w PresentMon.** Na razie tylko opt-in; włączenie domyślne dopiero po pomiarze narzutu.
- **#9 Utrzymanie ekranu.** Domyślnie włączone z możliwością wyłączenia czy wyłączone z możliwością włączenia? Czy pomijać grę, która sama zgłasza żądanie?
- **#10 Domyślne przypinanie gry.** Proponuję „włączone” przy migracji, czyli bez zmiany zachowania.
- **#13 Okno przekazania.** Jak długie domyślnie? Dłuższe okno oznacza późniejsze przywrócenie tła.
- **#14 Odświeżanie.** Mignięcie ekranu i wiele monitorów. Czy w ogóle chcemy tej funkcji?
- **#15 Czas gry.** Czy liczyć do wyjścia z gry, a nie do końca przywracania? Jak godzić wartość z ogólnym czasem gry raportowanym przez provider (np. Steam)?
- **#16 Watcher reguł tła.** Opt-in, odpytywanie co ustalony interwał.
- **#17 Faza 2 telemetrii GPU:** zgoda wlasciciela 2026-10-31 na NVML i ADL2: opt-in OFF, tylko podpisane biblioteki NVIDIA/AMD z System32, tylko proces UI i widoczna nakladka. Zaimplementowano; Intel IGCL poza zakresem.

## What we could not cover

- **Repozytoria bez przetrwałej propozycji:** WinMemoryCleaner, guapow i TimerResolution nie dały żadnego wyniku. Process Governor i PC-Tuning dały tylko propozycje odrzucone, a PC-Tuning nie znaleziono w klonach `oss-research`. Lista „Repos without result” na wejściu była pusta, więc ta lista pochodzi z porównania z listą badanych repozytoriów.
- **Odrzucone, ale warte ponownego spojrzenia po pomiarze:**
  - Flaga IGNORE_TIMER_RESOLUTION (0x4) dla procesów tła. Najpierw zmierzyć `NtQueryTimerResolution` i liczbę wybudzeń z aplikacją wymagającą precyzyjnego timera w tle. Mechanizm byłby tani, bo #2 i tak przerabia kontroler throttlingu na maskę.
  - Zwolnienie przypięcia przy nasyceniu, ale tylko na ścieżce fallbacku z twardą maską affinity.
  - Zdarzenia procesów z ETW zamiast odpytywania. Wraca tylko wtedy, gdy opóźnienie wykrycia (do 6 s) okaże się problemem dla gracza.
  - Start gry w stanie wstrzymanym. Wraca tylko po teście A/B pierwszych sekund gry.
  - Sonda rozdzielczości timera. Bez funkcji, która korzystałaby z jej wyniku, nie ma wartości.
- **Niezweryfikowane dowody źródłowe:** prawie żaden werdykt nie otworzył klonów (ThreadPilot, ProcGovernor, Playnite, AutoActions, Feral, LibreHardwareMonitor, Mem Reduct). Niepotwierdzone są też numery linii w `LocalGameSessionOrchestrator.cs` (530, 1954-2012, 4028-4040), połączenie z `PresentMonBenchmarkCapture` oraz nazwy kolumn CSV w PresentMon 2.5.1.
- **Poza zakresem wszystkich propozycji:** HDR i auto-HDR (AutoActions wspomina je tylko jako „później”), standby list i polityka czyszczenia pamięci poza samym cooldownem, sieć i opóźnienie wejścia. Żadne z badanych repozytoriów nie dało tu wyniku ocenionego w tej rundzie.

**Co zmieniłoby ranking:** pomiar PresentMon pokazujący realny wpływ #5, #11 lub #16 na 1% low podniósłby je wyżej. Dowód, że PresentMon 2.5.1 nie zgłasza utraty zdarzeń na stderr, obniżyłby #7.

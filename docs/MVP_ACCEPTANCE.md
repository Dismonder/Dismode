# Macierz akceptacji MVP GameShift

Źródło: sekcja 39 specyfikacji z załącznika zadania.  
Aktualizacja: 2026-07-29.

| Wymaganie MVP | Stan | Dowód / brakujący dowód |
| --- | --- | --- |
| WinUI 3 | Gotowe | Natywne, niepodwyższone UI x64; opublikowane XBF/PRI; responsywne okno `GameShift`. |
| SessionHost | Gotowe funkcjonalnie | Osobny backend wymagający zgody UAC, chroniony pipe użytkownika, plan i cykl sesji, recovery po restarcie hosta. UI pozostaje niepodwyższone. |
| SystemAgent | Częściowe | Osobny proces i chroniony pipe systemowy działają read-only. Instalacja jako usługa Windows i recovery przy starcie systemu wymagają kontrolowanej VM. |
| Zabezpieczone Named Pipes | Gotowe lokalnie | ACL ograniczone do SID użytkownika, LocalSystem i administratorów; wersja, SID, timestamp, replay, rozmiar i zamknięty katalog komend są walidowane. Brakuje testu obcego użytkownika na VM. |
| Procesy | Gotowe dla sesji użytkownika | Pełna tożsamość, klasyfikacja, monitoring drzewa gry i produkcyjny plan obejmują łagodne zamknięcie/przywrócenie oraz odwracalne `BelowNormal` i EcoQoS. Potomek gry pozostaje śledzony po wyjściu launchera i restarcie SessionHost. Procesy systemowe, launchery, anti-cheat, Discord/OBS i GameShift są chronione. |
| Podstawowa analiza usług | Gotowe read-only | Enumeracja stanu, konfiguracji, triggerów i zależności działa bez mutacji. |
| EcoQoS | Gotowe w klikalnym wydaniu | Jawny tryb `BelowNormal + EcoQoS` jest domyślną bezpieczną rekomendacją. Priorytet i EcoQoS mają osobne ActionId, snapshoty, weryfikację i rollback; użytkownik nadal może wybrać samo `BelowNormal`. |
| Zmiana priorytetu | Gotowe w klikalnym wydaniu | Gra: `AboveNormal` lub jawne `High`; tło: wyłącznie `Normal/BelowNormal → BelowNormal`. Produkcyjna akcja `High` została potwierdzona niezależnym odczytem klasy procesu z Windows i przywrócona do `Normal`. `Realtime` jest zablokowany przez Hard Safety. |
| Pomiar FPS przez PresentMon | Gotowe w klikalnym wydaniu | Oficjalny PresentMon 2.5.1 (MIT) jest częścią wydania i przechodzi kontrolę rozmiaru oraz SHA-256. SessionHost uruchamia go automatycznie wyłącznie dla śledzonych PID, parsuje CSV v2 i kończy miernik razem z sesją. HUD pokazuje się tylko nad aktywną grą i ma przełącznik, natywne krycie całego okna z odczytem zwrotnym alpha, skalę oraz wybór rogu. Historia zapisuje średni FPS/ms, najniższą próbkę FPS, najwyższą próbkę ms i liczbę rzeczywistych próbek; brak danych nie jest estymowany. |
| Łagodne zamykanie aplikacji | Gotowe w klikalnym wydaniu | Standardowe żądanie zamknięcia, timeout, brak `Kill`, ponowne uruchomienie tego samego zweryfikowanego EXE i automatyczne recovery. |
| Zatrzymywanie zatwierdzonych usług | Częściowe | Snapshot, zgoda, Hard Safety, zależności i rollback są przetestowane na symulatorze. Brakuje testowej usługi i macierzy crash/restart na VM, więc RPC pozostaje zablokowane. |
| Plan zasilania | Częściowe | Własny klon, aktywacja, konflikt i rollback są przetestowane na symulatorze. Brakuje walidacji na VM, więc RPC pozostaje zablokowane. |
| Biblioteka i ręczny profil gry | Gotowe lokalnie | Automatyczne wykrywanie Steam, Epic, GOG, Roblox i dostępnych instalacji Xbox, usuwanie nieaktualnych wersji Robloxa oraz ręczny EXE; pełna ścieżka, argumenty bez shella, SHA-256 i lokalny SQLite. Play stosuje zapisany priorytet i wcześniej zatwierdzone reguły procesów przez SessionHost; reguły są ponownie dopasowywane po pełnej ścieżce i walidowane, a nieaktualne cele pomijane fail-closed. Chronione obrazy Xbox bez bezpośredniego EXE są pomijane. |
| Snapshot | Gotowe w rdzeniu | Trwały snapshot jest zapisywany przed kontrolowaną zmianą; UI sesji zapisuje checkpoint przed startem gry. |
| Journal | Gotowe | Niezależny, append-only JSONL z łańcuchem SHA-256, write-through i flush przed mutacją. |
| Rollback | Gotowe na kontrolowanych akcjach | Idempotentny rollback w odwrotnej kolejności, trójstronne uzgadnianie i zachowanie zmian zewnętrznych. |
| Recovery po restarcie | Częściowe | Restart SessionHost i rzeczywiste odzyskanie aktywnej gry są potwierdzone także wtedy, gdy launcher zniknął, a działa potomny proces gry. Restart usługi i całego systemu wymaga VM. |
| Historia i reguły per gra | Gotowe | Parametryzowany SQLite v5, migracje zachowujące dane, retencja 90 dni, trwały priorytet gry i reguły `zamknij / obniż / obniż + EcoQoS / ignoruj` per ścieżka procesu. Starsze sesje bez pomiaru pozostają jawnie oznaczone jako brak danych. |
| Twarda polityka ochronna | Gotowe w rdzeniu | Chronione cele, brak recovery, niezatwierdzone działania i ryzykowne usługi są blokowane testami. |
| Klikalne uruchomienie | Gotowe lokalnie | `artifacts\GameShift-App\GameShift.exe` i skrót `GameShift` na pulpicie; wydanie protokołu v4 prosi o UAC dla SessionHost, uruchamia jedno zmaksymalizowane UI i nie dubluje procesów. |
| Dostępność UI | Częściowe | Jasny/ciemny/systemowy motyw, opisowe statusy, AutomationProperties i nazwy elementów list. Pozostały ręczne testy Narratora, wysokiego kontrastu i skalowania 300%. |

## Warunek aktywacji mutacji systemowych

Mutujące RPC usług i planu zasilania nie mogą zostać włączone tylko dlatego,
że adaptery i testy symulacyjne są zielone. Wymagane są:

1. własna testowa usługa Windows;
2. kontrolowana VM;
3. crash injection przed i po każdym trwałym checkpointcie;
4. restart SystemAgent oraz systemu;
5. potwierdzone przywrócenie 100% kontrolowanych zmian;
6. brak trwałej zmiany bez krótkotrwałej, powiązanej z sesją zgody.

Do czasu spełnienia tego warunku klikalne wydanie nie zatrzymuje usług i nie
przełącza planu zasilania. Rzeczywiste akcje procesów użytkownika są aktywne,
ale zawsze przechodzą klasyfikację, pełną walidację tożsamości, journal,
weryfikację i idempotentne przywracanie.

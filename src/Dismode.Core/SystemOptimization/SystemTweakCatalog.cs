using Dismode.Contracts.SystemOptimization;

namespace Dismode.Core.SystemOptimization;

public static class SystemTweakCatalog
{
    private const string UnsupportedReason =
        "Brak niezależnego i zweryfikowanego adaptera dla tego ustawienia w bieżącym wydaniu.";
    private const string ProcessSource =
        "https://learn.microsoft.com/windows/win32/procthread/process-priority-class";
    private const string PowerThrottlingSource =
        "https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation";
    private const string PowerSource =
        "https://learn.microsoft.com/windows-hardware/design/device-experiences/powercfg-command-line-options";
    private const string MmcssSource =
        "https://learn.microsoft.com/windows/win32/procthread/multimedia-class-scheduler-service";
    private const string GraphicsSource =
        "https://learn.microsoft.com/windows-hardware/drivers/display/timeout-detection-and-recovery";
    private const string GameModeSource =
        "https://learn.microsoft.com/windows/uwp/gaming/game-mode-and-your-game";
    private const string NetworkSource =
        "https://learn.microsoft.com/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics";
    private const string MemorySource =
        "https://learn.microsoft.com/windows/win32/memory/memory-management-functions";
    private const string FileSystemSource =
        "https://learn.microsoft.com/windows-server/administration/windows-commands/fsutil-behavior";
    private const string ServiceSource =
        "https://learn.microsoft.com/windows/win32/services/service-control-manager";
    private const string UiSource =
        "https://learn.microsoft.com/windows/apps/design/signature-experiences/motion";

    public static IReadOnlyList<TweakDefinition> CreateDefault() =>
    [
        Supported(
            "process.game.priority",
            "Priorytet procesu gry",
            "Procesy",
            "Ustawia AboveNormal tylko dla zweryfikowanego procesu gry i przywraca stan po sesji.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["above-normal"],
            "windows.process-priority",
            ProcessSource),
        Supported(
            "process.game.power-throttling",
            "Power Throttling procesu gry",
            "Procesy",
            "Wyłącza Power Throttling tylko dla zweryfikowanego procesu gry.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["disabled"],
            "windows.process-power-throttling",
            PowerThrottlingSource),
        Unsupported(
            "process.background.priority",
            "Priorytet zatwierdzonego procesu tła",
            "Procesy",
            "Obniża priorytet wybranego procesu tła bez obejmowania procesów systemowych.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["below-normal"],
            ProcessSource,
            requiresTarget: true),
        Unsupported(
            "process.background.ecoqos",
            "EcoQoS zatwierdzonego procesu tła",
            "Procesy",
            "Włącza EcoQoS dla pojedynczego, zatwierdzonego procesu tła.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["enabled"],
            PowerThrottlingSource,
            requiresTarget: true),
        Unsupported(
            "application.background.close-restore",
            "Zamknięcie i odtworzenie aplikacji tła",
            "Procesy",
            "Łagodnie zamyka osobno zatwierdzoną aplikację i odtwarza ją po sesji.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["close-for-session"],
            ProcessSource,
            requiresTarget: true),
        Unsupported(
            "power.owned-session-scheme",
            "Tymczasowy plan zasilania Dismode",
            "CPU i zasilanie",
            "Aktywuje należący do Dismode klon planu wyłącznie po pomiarze A/B i kontroli zasilania.",
            SystemTweakRisk.Safe,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["measured-clone"],
            PowerSource),
        Unsupported(
            "power.cpu.boost-mode",
            "Tryb boost procesora",
            "CPU i zasilanie",
            "Zmienia politykę boost w należącym do Dismode planie zasilania.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["efficient-enabled", "aggressive", "disabled"],
            PowerSource),
        Unsupported(
            "power.cpu.core-parking",
            "Core parking",
            "CPU i zasilanie",
            "Zmienia minimalny udział aktywnych rdzeni w planie zasilania.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["windows-default", "100-percent"],
            PowerSource),
        Unsupported(
            "power.cpu.epp",
            "CPPC / EPP",
            "CPU i zasilanie",
            "Zmienia preferencję wydajność–energia na zgodnym sprzęcie.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["windows-default", "performance", "balanced"],
            PowerSource),
        Unsupported(
            "power.cpu.processor-state",
            "Minimalny i maksymalny stan procesora",
            "CPU i zasilanie",
            "Modyfikuje ograniczenia stanu procesora tylko w zarządzanym planie.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["windows-default", "100-percent"],
            PowerSource),
        Unsupported(
            "power.pcie.aspm",
            "PCIe ASPM",
            "CPU i zasilanie",
            "Testuje politykę oszczędzania energii łącza PCIe.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "off", "moderate", "maximum"],
            PowerSource),
        Unsupported(
            "power.usb.selective-suspend",
            "USB selective suspend",
            "CPU i zasilanie",
            "Testuje selektywne wstrzymywanie USB w zarządzanym planie.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["windows-default", "enabled", "disabled"],
            PowerSource),
        Unsupported(
            "scheduler.mmcss.games",
            "Profil MMCSS Games",
            "Scheduler i MMCSS",
            "Testuje udokumentowane wartości zadania Games bez wyłączania MMCSS.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.GameRestart,
            ["windows-default", "latency"],
            MmcssSource),
        Unsupported(
            "scheduler.foreground-quantum",
            "Foreground quantum",
            "Scheduler i MMCSS",
            "Testuje preferencję planisty dla aplikacji pierwszoplanowej.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "foreground"],
            MmcssSource),
        Unsupported(
            "scheduler.global-power-throttling",
            "Globalny Power Throttling",
            "Scheduler i MMCSS",
            "Pokazuje globalną politykę, ale nie zastępuje bezpiecznych ustawień per-proces.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "disabled"],
            PowerThrottlingSource),
        Unsupported(
            "graphics.hags",
            "Hardware-accelerated GPU scheduling",
            "GPU i DWM",
            "Testuje HAGS na zgodnym sterowniku i systemie.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            GraphicsSource),
        Unsupported(
            "graphics.dwm.mpo",
            "Multiplane Overlay",
            "GPU i DWM",
            "Pokazuje stan MPO; wykonanie wymaga potwierdzonego adaptera dla bieżącego sterownika.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            GraphicsSource),
        Unsupported(
            "graphics.device.msi",
            "MSI urządzenia GPU",
            "GPU i DWM",
            "Zmienia tryb przerwań tylko dla jednoznacznie zidentyfikowanego urządzenia.",
            SystemTweakRisk.Dangerous,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            GraphicsSource,
            requiresTarget: true),
        Unsupported(
            "graphics.vendor.official-profile",
            "Oficjalny profil producenta GPU",
            "GPU i DWM",
            "Używa wyłącznie oficjalnego, wykrywalnego adaptera producenta.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.GameRestart,
            ["windows-default", "performance"],
            GraphicsSource,
            requiresTarget: true),
        Unsupported(
            "gaming.game-mode",
            "Tryb gry Windows",
            "Tryb gry i przechwytywanie",
            "Testuje ustawienie Trybu gry bez wyłączania funkcji bezpieczeństwa.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.GameRestart,
            ["windows-default", "enabled", "disabled"],
            GameModeSource),
        Unsupported(
            "gaming.background-capture",
            "Przechwytywanie w tle Game DVR",
            "Tryb gry i przechwytywanie",
            "Testuje wpływ nagrywania w tle; nie usuwa aplikacji ani danych użytkownika.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.GameRestart,
            ["windows-default", "enabled", "disabled"],
            GameModeSource),
        UnsupportedNetwork(
            "network.rss",
            "Receive Side Scaling",
            ["windows-default", "enabled", "disabled"]),
        UnsupportedNetwork(
            "network.rsc",
            "Receive Segment Coalescing",
            ["windows-default", "enabled", "disabled"]),
        UnsupportedNetwork(
            "network.tcp.autotuning",
            "TCP receive autotuning",
            ["windows-default", "normal", "disabled"]),
        UnsupportedNetwork(
            "network.tcp.ecn",
            "Explicit Congestion Notification",
            ["windows-default", "enabled", "disabled"]),
        UnsupportedNetwork(
            "network.tcp.fast-open",
            "TCP Fast Open",
            ["windows-default", "enabled", "disabled"]),
        UnsupportedNetwork(
            "network.tcp.timestamps",
            "TCP timestamps",
            ["windows-default", "enabled", "disabled"]),
        UnsupportedNetwork(
            "network.tcp.nagle-ack",
            "Nagle / ACK dla konkretnego adaptera",
            ["windows-default", "latency"],
            SystemTweakRisk.Dangerous),
        Unsupported(
            "memory.compression",
            "Kompresja pamięci",
            "Pamięć",
            "Testuje systemową kompresję pamięci z pełnym readbackiem stanu.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            MemorySource),
        Unsupported(
            "memory.system-cache-policy",
            "Polityka cache systemowego",
            "Pamięć",
            "Testuje udokumentowaną politykę cache; nie czyści pamięci na ślepo.",
            SystemTweakRisk.Dangerous,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "programs", "system-cache"],
            MemorySource),
        Unsupported(
            "filesystem.ntfs.8dot3",
            "Tworzenie nazw 8.3",
            "System plików",
            "Testuje politykę nowych nazw 8.3 bez usuwania istniejących nazw.",
            SystemTweakRisk.Dangerous,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            FileSystemSource),
        Unsupported(
            "filesystem.ntfs.last-access",
            "Aktualizacja Last Access",
            "System plików",
            "Testuje systemową politykę aktualizacji czasu ostatniego dostępu.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "enabled", "disabled"],
            FileSystemSource),
        Supported(
            "power.hibernate",
            "Hibernacja Windows",
            "System plików",
            "Włącza lub wyłącza hibernację stałym poleceniem systemowym i sprawdza stan po operacji.",
            SystemTweakRisk.Dangerous,
            SystemTweakScope.Global,
            RestartRequirement.None,
            ["enabled", "disabled"],
            "windows.hibernate",
            PowerSource),
        Unsupported(
            "service.approved.temporary-stop",
            "Tymczasowe zatrzymanie zatwierdzonej usługi",
            "Usługi i zadania",
            "Zatrzymuje pojedynczą usługę wybraną z aktualnego inventory i odtwarza pełny stan.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["stopped-for-session"],
            ServiceSource,
            requiresTarget: true),
        Unsupported(
            "task.approved.temporary-disable",
            "Tymczasowe wyłączenie zatwierdzonego zadania",
            "Usługi i zadania",
            "Wyłącza pojedyncze zadanie wybrane z inventory i zachowuje jego pełny stan.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.GameSession,
            RestartRequirement.None,
            ["disabled-for-session"],
            ServiceSource,
            requiresTarget: true),
        Unsupported(
            "telemetry.approved.setting",
            "Pojedyncze ustawienie telemetryczne",
            "Usługi i zadania",
            "Udostępnia tylko jawnie zinwentaryzowane i odwracalne ustawienia niesecurityczne.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            ["windows-default", "reduced"],
            ServiceSource,
            requiresTarget: true),
        Unsupported(
            "ui.menu-delay",
            "Opóźnienie menu",
            "Responsywność UI",
            "Testuje opóźnienie rozwijania menu dla bieżącego użytkownika.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.SignOut,
            ["windows-default", "responsive"],
            UiSource),
        Unsupported(
            "ui.animations",
            "Animacje interfejsu Windows",
            "Responsywność UI",
            "Testuje udokumentowane preferencje animacji bez zmiany motywu użytkownika.",
            SystemTweakRisk.Experimental,
            SystemTweakScope.Global,
            RestartRequirement.SignOut,
            ["windows-default", "reduced"],
            UiSource),
        Blocked(
            "security.defender.disable",
            "Wyłączenie Microsoft Defender",
            "Dismode nigdy nie wyłącza ochrony antywirusowej.",
            "https://learn.microsoft.com/defender-endpoint/prevent-changes-to-security-settings-with-tamper-protection"),
        Blocked(
            "security.firewall.disable",
            "Wyłączenie zapory Windows",
            "Dismode nigdy nie wyłącza zapory sieciowej.",
            "https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall"),
        Blocked(
            "security.windows-update.disable",
            "Wyłączenie Windows Update",
            "Dismode nigdy nie wyłącza aktualizacji zabezpieczeń.",
            "https://learn.microsoft.com/windows/deployment/update/windows-update-overview"),
        Blocked(
            "security.bitlocker.disable",
            "Wyłączenie BitLocker",
            "Dismode nigdy nie zmienia ochrony dysku BitLocker.",
            "https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker"),
        Blocked(
            "security.tamper-protection.disable",
            "Wyłączenie Tamper Protection",
            "Tamper Protection może zignorować pozornie przyjętą zmianę; Dismode nie próbuje jej obchodzić.",
            "https://learn.microsoft.com/defender-endpoint/prevent-changes-to-security-settings-with-tamper-protection"),
        Blocked(
            "security.secure-boot.disable",
            "Wyłączenie Secure Boot",
            "Dismode nigdy nie obniża zaufania rozruchu.",
            "https://learn.microsoft.com/windows-hardware/design/device-experiences/oem-secure-boot"),
        Blocked(
            "security.hvci.disable",
            "Wyłączenie HVCI",
            "Dismode nigdy nie wyłącza integralności kodu chronionej przez hypervisor.",
            "https://learn.microsoft.com/windows-hardware/design/device-experiences/oem-hvci-enablement"),
        Blocked(
            "security.exploit-mitigations.disable",
            "Wyłączenie mitigacji exploitów",
            "Dismode nigdy nie osłabia mitigacji procesu ani systemu.",
            "https://learn.microsoft.com/defender-endpoint/exploit-protection-reference"),
        Blocked(
            "security.anticheat.disable",
            "Wyłączenie anti-cheat",
            "Dismode nigdy nie modyfikuje ani nie omija anti-cheat.",
            "https://learn.microsoft.com/windows/security/application-security/application-control/windows-defender-application-control"),
        Blocked(
            "reliability.whea.disable",
            "Wyłączenie WHEA",
            "WHEA raportuje i obsługuje błędy sprzętowe; ta funkcja pozostaje chroniona.",
            "https://learn.microsoft.com/windows-hardware/drivers/whea/components-of-the-windows-hardware-error-architecture"),
        Blocked(
            "reliability.dpc-watchdog.disable",
            "Wyłączenie DPC watchdog",
            "Dismode nie wyłącza mechanizmów wykrywania zablokowanych procedur jądra.",
            "https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x133-dpc-watchdog-violation"),
        BlockedBcd(
            "timer.bcd.useplatformclock",
            "BCD useplatformclock"),
        BlockedBcd(
            "timer.bcd.useplatformtick",
            "BCD useplatformtick"),
        BlockedBcd(
            "timer.bcd.disabledynamictick",
            "BCD disabledynamictick"),
        BlockedBcd(
            "timer.hpet.force",
            "Wymuszenie HPET"),
    ];

    private static TweakDefinition Supported(
        string id,
        string displayName,
        string category,
        string description,
        SystemTweakRisk risk,
        SystemTweakScope scope,
        RestartRequirement restart,
        IReadOnlyList<string> values,
        string adapter,
        string source,
        IReadOnlyList<string>? conflicts = null,
        bool requiresTarget = false) =>
        new(
            id,
            Revision: 1,
            displayName,
            category,
            description,
            risk,
            scope,
            restart,
            SystemTweakAvailability.Supported,
            values,
            conflicts ?? [],
            adapter,
            source,
            "Dismode zapisuje stan original/applied/current i wykonuje trójstronne przywracanie.",
            BlockingReason: null,
            MinimumWindowsBuild: 22631,
            MaximumWindowsBuild: null,
            requiresTarget);

    private static TweakDefinition Unsupported(
        string id,
        string displayName,
        string category,
        string description,
        SystemTweakRisk risk,
        SystemTweakScope scope,
        RestartRequirement restart,
        IReadOnlyList<string> values,
        string source,
        IReadOnlyList<string>? conflicts = null,
        bool requiresTarget = false) =>
        new(
            id,
            Revision: 1,
            displayName,
            category,
            description,
            risk,
            scope,
            restart,
            SystemTweakAvailability.Unsupported,
            values,
            conflicts ?? [],
            ExecutionAdapterId: null,
            source,
            "Brak mutacji; stan systemu pozostaje bez zmian.",
            UnsupportedReason,
            MinimumWindowsBuild: 22631,
            MaximumWindowsBuild: null,
            requiresTarget);

    private static TweakDefinition UnsupportedNetwork(
        string id,
        string displayName,
        IReadOnlyList<string> values,
        SystemTweakRisk risk = SystemTweakRisk.Experimental) =>
        Unsupported(
            id,
            displayName,
            "Sieć",
            "Test dotyczy wyłącznie adaptera wybranego z aktualnego inventory i wymaga niezależnego readbacku.",
            risk,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            values,
            NetworkSource,
            requiresTarget: true);

    private static TweakDefinition Blocked(
        string id,
        string displayName,
        string reason,
        string source) =>
        new(
            id,
            Revision: 1,
            displayName,
            "Hard Safety Policy",
            reason,
            SystemTweakRisk.Blocked,
            SystemTweakScope.Global,
            RestartRequirement.WindowsRestart,
            SystemTweakAvailability.BlockedByPolicy,
            ["blocked"],
            [],
            ExecutionAdapterId: null,
            source,
            "Dismode nie wykonuje tej zmiany, więc przywracanie nie jest potrzebne.",
            reason);

    private static TweakDefinition BlockedBcd(
        string id,
        string displayName) =>
        Blocked(
            id,
            displayName,
            "Modyfikacje BCD i timerów mogą uniemożliwić uruchomienie systemu; Dismode ich nie wykonuje.",
            "https://learn.microsoft.com/windows-hardware/drivers/devtest/bcdedit--set");
}

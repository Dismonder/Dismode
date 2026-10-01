#ifndef SourceDir
  #error SourceDir must be provided by Build-Installer.ps1
#endif

#ifndef OutputDir
  #error OutputDir must be provided by Build-Installer.ps1
#endif

#ifndef RepoRoot
  #error RepoRoot must be provided by Build-Installer.ps1
#endif

#ifndef AppVersion
  #error AppVersion must be provided by Build-Installer.ps1
#endif

#ifndef MemoryOptimizerDir
  #error MemoryOptimizerDir must be provided by Build-Installer.ps1
#endif

[Setup]
AppId={{8C8B533E-5B8B-46A7-B5F5-B3C2E5F7B9A1}
AppName=Dismode
AppVersion={#AppVersion}
AppVerName=Dismode {#AppVersion} Gaming Edition
AppPublisher=Dismode Research Project
AppPublisherURL=https://dismode-update-service-dev.dismonder.workers.dev/
AppSupportURL=https://dismode-update-service-dev.dismonder.workers.dev/support
AppUpdatesURL=https://dismode-update-service-dev.dismonder.workers.dev/
AppCopyright=Copyright (C) 2026 Dismode Research Project
AppComments=Lokalny, mierzalny i odwracalny optymalizator sesji gry dla Windows 11.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
Compression=lzma2/ultra64
DefaultDirName={autopf}\Dismode
DefaultGroupName=Dismode
DisableProgramGroupPage=yes
InfoBeforeFile={#RepoRoot}\installer\INSTALLER-NOTES.txt
LicenseFile={#RepoRoot}\installer\LICENSE-PL.txt
MinVersion=10.0.22631
OutputBaseFilename=Dismode-Setup-{#AppVersion}-win-x64
OutputDir={#OutputDir}
PrivilegesRequired=admin
RestartApplications=no
SetupIconFile={#RepoRoot}\assets\branding\Dismode.ico
SetupLogging=yes
SetupMutex=Dismode.Setup.8C8B533E5B8B46A7B5F5B3C2E5F7B9A1
SolidCompression=yes
UninstallDisplayIcon={app}\Dismode.exe
UninstallDisplayName=Dismode {#AppVersion} Gaming Edition
UsePreviousAppDir=yes
UsePreviousSetupType=yes
UsePreviousTasks=yes
VersionInfoCompany=Dismode Research Project
VersionInfoCopyright=Copyright (C) 2026 Dismode Research Project
VersionInfoDescription=Instalator Dismode {#AppVersion} Gaming Edition
VersionInfoProductName=Dismode {#AppVersion} Gaming Edition
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
WizardStyle=modern

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Types]
Name: "full"; Description: "Pełna instalacja (zalecana)"
Name: "compact"; Description: "Tylko część gamingowa Dismode"
Name: "custom"; Description: "Instalacja niestandardowa"; Flags: iscustom

[Components]
Name: "gaming"; Description: "Dismode Gaming Edition"; Types: full compact custom; Flags: fixed
Name: "memoryoptimizer"; Description: "Dismode Memory Optimizer (GPL-3.0, niezależny tray i usługa)"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; GroupDescription: "Dodatkowe skróty:"; Flags: checkedonce

[Files]
Source: "{#MemoryOptimizerDir}\tools\Prepare-MemoryOptimizerUpdate.ps1"; Flags: dontcopy; Components: memoryoptimizer
Source: "{#RepoRoot}\installer\system-agent\Prepare-SystemAgentMaintenance.ps1"; Flags: dontcopy
Source: "{#RepoRoot}\installer\migration\Remove-LegacyGameShift.ps1"; Flags: dontcopy
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: gaming
Source: "{#RepoRoot}\installer\system-agent\Install-SystemAgent.ps1"; DestDir: "{app}\SystemIntegration\system-agent"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\system-agent\Prepare-SystemAgentMaintenance.ps1"; DestDir: "{app}\SystemIntegration\system-agent"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\system-agent\Uninstall-SystemAgent.ps1"; DestDir: "{app}\SystemIntegration\system-agent"; Flags: ignoreversion
Source: "{#SourceDir}\trusted-signers.json"; DestDir: "{commonappdata}\Dismode"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\PRIVACY-PL.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MemoryOptimizerDir}\*"; DestDir: "{app}\MemoryOptimizer"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: memoryoptimizer

[Dirs]
Name: "{commonappdata}\Dismode\Shared"; Permissions: users-modify; Components: memoryoptimizer
Name: "{commonappdata}\Dismode\MemoryOptimizer"; Components: memoryoptimizer

[InstallDelete]
Type: files; Name: "{app}\*.pdb"
Type: files; Name: "{app}\System.Windows.Forms*.dll"
Type: files; Name: "{app}\cs\System.Windows.Forms*.dll"
Type: files; Name: "{app}\de\System.Windows.Forms*.dll"
Type: files; Name: "{app}\es\System.Windows.Forms*.dll"
Type: files; Name: "{app}\fr\System.Windows.Forms*.dll"
Type: files; Name: "{app}\it\System.Windows.Forms*.dll"
Type: files; Name: "{app}\ja\System.Windows.Forms*.dll"
Type: files; Name: "{app}\ko\System.Windows.Forms*.dll"
Type: files; Name: "{app}\pl\System.Windows.Forms*.dll"
Type: files; Name: "{app}\pt-BR\System.Windows.Forms*.dll"
Type: files; Name: "{app}\ru\System.Windows.Forms*.dll"
Type: files; Name: "{app}\tr\System.Windows.Forms*.dll"
Type: files; Name: "{app}\zh-Hans\System.Windows.Forms*.dll"
Type: files; Name: "{app}\zh-Hant\System.Windows.Forms*.dll"

[Icons]
Name: "{group}\Dismode"; Filename: "{app}\Dismode.exe"; WorkingDir: "{app}"
Name: "{group}\Dismode System Optimizer"; Filename: "{app}\SystemOptimizer\Dismode.SystemOptimizer.exe"; WorkingDir: "{app}\SystemOptimizer"
Name: "{autodesktop}\Dismode"; Filename: "{app}\Dismode.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Classes\exefile\shell\Dismode"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Uruchom przez Dismode"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\exefile\shell\Dismode"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Dismode.exe,0"
Root: HKLM; Subkey: "Software\Classes\exefile\shell\Dismode"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKLM; Subkey: "Software\Classes\exefile\shell\Dismode\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Dismode.exe"" --launch-through-dismode ""%1"" --background"

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\MemoryOptimizer\tools\Install-MemoryOptimizer.ps1"" -InstallDirectory ""{app}\MemoryOptimizer"""; WorkingDir: "{app}\MemoryOptimizer"; StatusMsg: "Instalowanie niezależnej usługi Memory Optimizer..."; Flags: runhidden waituntilterminated; Components: memoryoptimizer
Filename: "{app}\MemoryOptimizer\Tray\Dismode.MemoryOptimizer.exe"; Parameters: "--background"; WorkingDir: "{app}\MemoryOptimizer\Tray"; Flags: nowait runasoriginaluser skipifsilent; Components: memoryoptimizer
Filename: "{app}\Dismode.exe"; Description: "Uruchom Dismode"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent shellexec; Components: gaming

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\MemoryOptimizer\tools\Uninstall-MemoryOptimizer.ps1"""; WorkingDir: "{app}\MemoryOptimizer"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveDismodeMemoryOptimizerService"

[Code]
var
  CustomSetupExitCode: Integer;
  IsAlreadyInstalled: Boolean;
  ExistingInstallPath: String;
  { Katalog wydania sprzed zmiany nazwy (GameShift); pusty, gdy go nie ma. }
  LegacyInstallPath: String;
  MaintenancePage: TInputOptionWizardPage;
  MemoryOptimizerGplPage: TOutputMsgMemoWizardPage;
  MaintenanceChoice: Integer;

function GetCustomSetupExitCode: Integer;
begin
  Result := CustomSetupExitCode;
end;

function IsWindows11OrLater: Boolean;
var
  WindowsVersion: TWindowsVersion;
begin
  GetWindowsVersionEx(WindowsVersion);
  Result :=
    (WindowsVersion.Major > 10) or
    ((WindowsVersion.Major = 10) and
     (WindowsVersion.Build >= 22631));
end;

{ Ten sam AppId obejmuje wydania sprzed zmiany nazwy, ktore instalowaly
  GameShift.exe zamiast Dismode.exe. }
function IsInstallDirectory(const Path: String): Boolean;
begin
  Result := (Path <> '') and DirExists(Path) and
    (FileExists(Path + '\Dismode.exe') or FileExists(Path + '\GameShift.exe'));
end;

function DetectExistingInstallation: Boolean;
var
  UninstallKey: String;
  InstallLocation: String;
begin
  Result := False;
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8C8B533E-5B8B-46A7-B5F5-B3C2E5F7B9A1}_is1';

  if RegQueryStringValue(HKLM, UninstallKey, 'InstallLocation', InstallLocation) then
  begin
    if IsInstallDirectory(RemoveBackslashUnlessRoot(InstallLocation)) then
    begin
      ExistingInstallPath := RemoveBackslashUnlessRoot(InstallLocation);
      Result := True;
      exit;
    end;
  end;

  if RegQueryStringValue(HKLM, UninstallKey, 'Inno Setup: App Path', InstallLocation) then
  begin
    if IsInstallDirectory(RemoveBackslashUnlessRoot(InstallLocation)) then
    begin
      ExistingInstallPath := RemoveBackslashUnlessRoot(InstallLocation);
      Result := True;
      exit;
    end;
  end;

  if IsInstallDirectory(ExpandConstant('{autopf}\Dismode')) then
  begin
    ExistingInstallPath := ExpandConstant('{autopf}\Dismode');
    Result := True;
    exit;
  end;

  if IsInstallDirectory(ExpandConstant('{autopf}\GameShift')) then
  begin
    ExistingInstallPath := ExpandConstant('{autopf}\GameShift');
    Result := True;
    exit;
  end;
end;

function InitializeSetup: Boolean;
begin
  CustomSetupExitCode := 0;
  ExistingInstallPath := '';
  LegacyInstallPath := '';
  IsAlreadyInstalled := DetectExistingInstallation;
  if IsAlreadyInstalled and
     FileExists(ExistingInstallPath + '\GameShift.exe') and
     (not FileExists(ExistingInstallPath + '\Dismode.exe')) then
    LegacyInstallPath := ExistingInstallPath;
  Result := True;
end;

procedure InitializeWizard;
begin
  MemoryOptimizerGplPage := CreateOutputMsgMemoPage(
    wpSelectComponents,
    'Dismode Memory Optimizer — GNU GPL v3',
    'Osobna informacja o opcjonalnym komponencie',
    'Memory Optimizer jest niezależnym programem GPL. Główny instalator ' +
    'jest agregatem dwóch programów.',
    'Derived from Windows Memory Cleaner 3.0.8 © Igor Mundstein.' + #13#10 +
    'Źródło upstream: https://github.com/IgorMundstein/WinMemoryCleaner' + #13#10 +
    'Licencja komponentu: GPL-3.0-only.' + #13#10#13#10 +
    'Instalowana wersja zawiera pełną licencję, listę modyfikacji oraz ' +
    'dokładne odpowiadające archiwum źródłowe z sumą SHA-256.' + #13#10#13#10 +
    'Usługa działa niezależnie po zamknięciu Dismode. Agresywne opcje ' +
    'są domyślnie ukryte i wymagają osobnej zgody w interfejsie.');

  if IsAlreadyInstalled then
  begin
    MaintenancePage := CreateInputOptionPage(
      wpWelcome,
      'Wykryto istniejącą instalację Dismode',
      'Wybierz operację, którą instalator ma przeprowadzić:',
      'Dismode jest już zainstalowany na tym komputerze.' + #13#10 +
      'Katalog: ' + ExistingInstallPath + #13#10#13#10 +
      'Wybierz jedną z dostępnych opcji konserwacji lub aktualizacji:',
      True, False);

    MaintenancePage.Add(
      'Aktualizuj (Zalecane) — Zaktualizuj pliki programu do najnowszej wersji. Twoje profile gier, historia i baza danych zostaną w pełni zachowane.');
    MaintenancePage.Add(
      'Napraw — Ponowna instalacja wszystkich składników, odświeżenie rejestracji menu Windows 11 i naprawa skrótów.');
    MaintenancePage.Add(
      'Odinstaluj — Bezpiecznie usuń program Dismode z komputera.');

    MaintenancePage.SelectedValueIndex := 0;
  end;

  if IsAlreadyInstalled and
     (GetPreviousData('MemoryOptimizerSelected', '1') = '0') then
    WizardSelectComponents('gaming');

  { Domyslny katalog i grupa starej nazwy przechodza na nowa; katalog
    wybrany recznie przez uzytkownika zostaje tam, gdzie byl. }
  if LegacyInstallPath <> '' then
  begin
    if CompareText(ExtractFileName(LegacyInstallPath), 'GameShift') = 0 then
      WizardForm.DirEdit.Text :=
        AddBackslash(ExtractFileDir(LegacyInstallPath)) + 'Dismode'
    else
      WizardForm.DirEdit.Text := LegacyInstallPath;
    if CompareText(WizardForm.GroupEdit.Text, 'GameShift') = 0 then
      WizardForm.GroupEdit.Text := 'Dismode';
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  if WizardIsComponentSelected('memoryoptimizer') then
    SetPreviousData(
      PreviousDataKey,
      'MemoryOptimizerSelected',
      '1')
  else
    SetPreviousData(
      PreviousDataKey,
      'MemoryOptimizerSelected',
      '0');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (MemoryOptimizerGplPage <> nil) and
     (PageID = MemoryOptimizerGplPage.ID) and
     (not WizardIsComponentSelected('memoryoptimizer')) then
    Result := True;
  if IsAlreadyInstalled then
  begin
    if (PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) then
      Result := True;
  end;
end;

function RemoveDeselectedMemoryOptimizer(
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := True;
  ErrorMessage := '';
  if WizardIsComponentSelected('memoryoptimizer') then
    exit;

  ScriptPath := ExpandConstant(
    '{app}\MemoryOptimizer\tools\Uninstall-MemoryOptimizer.ps1');
  if not FileExists(ScriptPath) then
    exit;

  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if (not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{app}\MemoryOptimizer'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode)) or (ResultCode <> 0) then
  begin
    ErrorMessage :=
      'Nie udało się bezpiecznie usunąć opcjonalnej usługi Memory Optimizer.';
    Result := False;
  end;
end;

function PrepareMemoryOptimizerForUpdate(
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := True;
  ErrorMessage := '';
  if not WizardIsComponentSelected('memoryoptimizer') then
    exit;

  ExtractTemporaryFile('Prepare-MemoryOptimizerUpdate.ps1');
  ScriptPath := ExpandConstant(
    '{tmp}\Prepare-MemoryOptimizerUpdate.ps1');
  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '" ' +
    '-InstallDirectory "' + ExpandConstant('{app}\MemoryOptimizer') + '"';
  if (not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{tmp}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode)) or (ResultCode <> 0) then
  begin
    ErrorMessage :=
      'Nie udało się bezpiecznie zatrzymać Memory Optimizer na czas aktualizacji.';
    Result := False;
  end;
end;

function PrepareSystemAgentForMaintenance(
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';
  ExtractTemporaryFile('Prepare-SystemAgentMaintenance.ps1');
  ScriptPath := ExpandConstant(
    '{tmp}\Prepare-SystemAgentMaintenance.ps1');
  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{tmp}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage :=
      'Nie udało się uruchomić kontrolowanego zatrzymania usługi System Optimizer.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorMessage :=
      'Usługa System Optimizer nie została bezpiecznie zatrzymana ' +
      '(kod ' + IntToStr(ResultCode) + ').';
    exit;
  end;

  Result := True;
end;

function RunInstalledSystemAgentScript(
  const ScriptName: String;
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';
  ScriptPath := ExpandConstant(
    '{app}\SystemIntegration\system-agent\' + ScriptName);
  if not FileExists(ScriptPath) then
  begin
    ErrorMessage := 'Brakuje skryptu usługi System Optimizer: ' + ScriptName;
    exit;
  end;

  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage := 'Nie udało się uruchomić skryptu usługi System Optimizer.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorMessage :=
      'Skrypt usługi System Optimizer zakończył się błędem ' +
      '(kod ' + IntToStr(ResultCode) + ').';
    exit;
  end;

  Result := True;
end;

function InstallSystemAgentService(var ErrorMessage: String): Boolean;
begin
  Result := RunInstalledSystemAgentScript(
    'Install-SystemAgent.ps1',
    ErrorMessage);
end;

function UninstallSystemAgentService(var ErrorMessage: String): Boolean;
begin
  Result := RunInstalledSystemAgentScript(
    'Uninstall-SystemAgent.ps1',
    ErrorMessage);
end;

function RunShellIntegrationScript(
  const ScriptName: String;
  const MissingScriptIsSuccess: Boolean;
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';

  if not IsWindows11OrLater then
  begin
    Result := True;
    exit;
  end;

  ScriptPath := ExpandConstant(
    '{app}\ShellIntegration\' + ScriptName);
  if not FileExists(ScriptPath) then
  begin
    Result := MissingScriptIsSuccess;
    if not Result then
      ErrorMessage :=
        'Brakuje skryptu bezpiecznej rejestracji menu Windows 11: ' +
        ScriptName;
    exit;
  end;

  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  if not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage :=
      'Nie udało się uruchomić integracji menu Windows 11.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorMessage :=
      'Windows odrzucił integrację nowego menu kontekstowego ' +
      '(kod ' + IntToStr(ResultCode) + ').';
    exit;
  end;

  Result := True;
end;

function SupportsSafeMaintenance(const HostPath: String): Boolean;
var
  VersionMS: Cardinal;
  VersionLS: Cardinal;
begin
  Result := GetVersionNumbers(HostPath, VersionMS, VersionLS) and
    ((VersionMS > 1) or
     ((VersionMS = 1) and (VersionLS >= 65536)));
end;

function PrepareInstallationForMaintenance(
  const InstallDir: String;
  const ProductName: String;
  const HostCommand: String;
  var ErrorMessage: String): Boolean;
var
  HostPath: String;
  LauncherPath: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';
  HostPath := InstallDir + '\' + ProductName + '.SessionHost.exe';
  LauncherPath := InstallDir + '\' + ProductName + '.exe';

  if not FileExists(LauncherPath) then
  begin
    Result := True;
    exit;
  end;

  if not FileExists(HostPath) then
  begin
    ErrorMessage :=
      'Istniejąca instalacja jest niepełna i nie zawiera bezpiecznej ' +
      'bramy recovery. Nie można automatycznie zamknąć jej składników.';
    exit;
  end;

  if not SupportsSafeMaintenance(HostPath) then
  begin
    ErrorMessage :=
      'Zainstalowana wersja jest starsza niż bezpieczny mechanizm ' +
      'automatycznej aktualizacji. Najpierw zakończ jej aktywną sesję.';
    exit;
  end;

  if not Exec(
      HostPath,
      HostCommand,
      InstallDir,
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage :=
      'Nie udało się uruchomić bezpiecznej bramy aktualizacji Dismode.';
    exit;
  end;

  if ResultCode = 0 then
  begin
    Result := True;
    exit;
  end;

  if ResultCode = 3 then
    ErrorMessage :=
      'Aktualizacja jest zablokowana, ponieważ journal zawiera aktywną ' +
      'lub niedokończoną sesję. Dismode nie zamknął gry.'
  else if ResultCode = 4 then
    ErrorMessage :=
      'Journal recovery nie przeszedł kontroli integralności.'
  else if ResultCode = 6 then
    ErrorMessage :=
      'System Optimizer nie jest gotowy do aktualizacji. Zakończ aktywny ' +
      'eksperyment lub profil gry i wykonaj wymagany restore.'
  else if ResultCode = 7 then
    ErrorMessage :=
      'Nie można odinstalować System Optimizer, ponieważ restore nie został ' +
      'potwierdzony. Usługa i dane recovery pozostają zachowane.'
  else if ResultCode = 8 then
    ErrorMessage :=
      'OptiScaler jest nadal zainstalowany w katalogach gier. Usunięcie ' +
      'Dismode zostawi tam jego pliki i zabierze jedyne narzędzie, które ' +
      'potrafi je stamtąd usunąć. Otwórz Dismode, usuń OptiScaler dla ' +
      'każdej gry, która go ma, i dopiero wtedy odinstaluj program.'
  else if ResultCode = 9 then
    ErrorMessage :=
      'Poprzednia wersja programu (GameShift) nadal działa albo trzyma ' +
      'swoją bazę profili lub dziennik recovery. Zamknij GameShift ' +
      '(ikona w zasobniku → Wyłącz GameShift) i uruchom instalator ponownie.'
  else
    ErrorMessage :=
      'Nie udało się zamknąć wyłącznie składników Dismode z katalogu ' +
      'instalacji. Gra i obce procesy nie zostały zakończone.';
end;

function PrepareInstalledDismodeForMaintenance(
  const HostCommand: String;
  var ErrorMessage: String): Boolean;
begin
  Result := PrepareInstallationForMaintenance(
    ExpandConstant('{app}'),
    'Dismode',
    HostCommand,
    ErrorMessage);
end;

function RunLegacyMigrationScript(
  const Phase: String;
  var ErrorMessage: String): Boolean;
var
  PowerShellPath: String;
  ScriptPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';
  ExtractTemporaryFile('Remove-LegacyGameShift.ps1');
  ScriptPath := ExpandConstant('{tmp}\Remove-LegacyGameShift.ps1');
  PowerShellPath := ExpandConstant(
    '{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters :=
    '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden ' +
    '-ExecutionPolicy Bypass -File "' + ScriptPath + '" ' +
    '-Phase ' + Phase + ' ' +
    '-LegacyInstallDirectory "' + LegacyInstallPath + '" ' +
    '-InstallDirectory "' + ExpandConstant('{app}') + '"';
  if not Exec(
      PowerShellPath,
      Parameters,
      ExpandConstant('{tmp}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage :=
      'Nie udało się uruchomić migracji instalacji GameShift.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorMessage :=
      'Migracja instalacji GameShift (' + Phase + ') zakończyła się ' +
      'błędem (kod ' + IntToStr(ResultCode) + ').';
    exit;
  end;

  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  UninstallerPath: String;
  ResultCode: Integer;
begin
  Result := True;
  if IsAlreadyInstalled and (MaintenancePage <> nil) and (CurPageID = MaintenancePage.ID) then
  begin
    MaintenanceChoice := MaintenancePage.SelectedValueIndex;
    if MaintenanceChoice = 2 then // Uninstall
    begin
      if MsgBox('Czy na pewno chcesz odinstalować program Dismode z tego komputera?', mbConfirmation, MB_YESNO) = IDYES then
      begin
        UninstallerPath :=
          AddBackslash(ExistingInstallPath) + 'unins000.exe';

        if FileExists(UninstallerPath) then
        begin
          ShellExec(
            'open',
            UninstallerPath,
            '',
            ExistingInstallPath,
            SW_SHOW,
            ewNoWait,
            ResultCode);
        end
        else
        begin
          MsgBox('Nie znaleziono pliku deinstalatora: ' + UninstallerPath, mbError, MB_OK);
        end;
        WizardForm.Close;
        Result := False;
        exit;
      end
      else
      begin
        Result := False;
        exit;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ShellError: String;
begin
  Result := '';
  { Stara wersja musi sama potwierdzic, ze jej dziennik recovery jest
    czysty, zanim zdejmiemy jej uslugi i przeniesiemy dane. }
  if LegacyInstallPath <> '' then
  begin
    if not PrepareInstallationForMaintenance(
        LegacyInstallPath,
        'GameShift',
        '--prepare-update',
        Result) then
      exit;

    if not RunLegacyMigrationScript('Prepare', Result) then
      exit;
  end;

  if not PrepareInstalledDismodeForMaintenance(
      '--prepare-update',
      Result) then
    exit;

  if not PrepareSystemAgentForMaintenance(Result) then
    exit;

  if not RemoveDeselectedMemoryOptimizer(Result) then
    exit;

  if not PrepareMemoryOptimizerForUpdate(Result) then
    exit;

  if not RunShellIntegrationScript(
      'Unregister-DismodeShell.ps1',
      True,
      ShellError) then
    Result := ShellError;
end;

function InitializeUninstall: Boolean;
var
  GateError: String;
  StepError: String;
  Remaining: String;
begin
  { Brama recovery nie moze byc slepym zaulkiem. Jesli restore sie nie uda,
    uzytkownik dostaje pelna informacje i decyduje sam. Dane recovery i tak
    nie sa kasowane przez deinstalacje, wiec zgoda niczego nie niszczy. }
  if not PrepareInstalledDismodeForMaintenance(
      '--prepare-uninstall',
      GateError) then
  begin
    if MsgBox(
        GateError + #13#10 + #13#10 +
        'Dane recovery zostana zachowane w %ProgramData%\Dismode oraz ' +
        '%LocalAppData%\Dismode i nie zostana usuniete, wiec mozesz je ' +
        'sprawdzic pozniej.' + #13#10 + #13#10 +
        'Czy mimo to usunac Dismode razem z uslugami? Pozostawienie ' +
        'programu w tym stanie oznacza, ze uslugi nadal beda uruchamiane ' +
        'przy starcie systemu.',
        mbConfirmation,
        MB_YESNO) <> IDYES then
    begin
      Result := False;
      exit;
    end;
  end;

  { Od tego miejsca deinstalacja idzie do konca. Kazdy krok wykonuje sie
    niezaleznie, zeby awaria jednego nie zostawila dzialajacej uslugi. }
  Result := True;
  Remaining := '';

  if not RunShellIntegrationScript(
      'Unregister-DismodeShell.ps1',
      True,
      StepError) then
    Remaining := Remaining + #13#10 + '- integracja z menu Eksploratora: ' +
      StepError;

  if not UninstallSystemAgentService(StepError) then
    Remaining := Remaining + #13#10 + '- usluga Dismode System Agent: ' +
      StepError;

  if Remaining <> '' then
    MsgBox(
      'Dismode zostanie usuniety, ale ponizszych elementow nie udalo sie ' +
      'usunac automatycznie:' + #13#10 + Remaining + #13#10 + #13#10 +
      'Usluge mozesz usunac recznie poleceniem uruchomionym jako ' +
      'administrator:' + #13#10 +
      '    sc.exe delete DismodeSystemAgent',
      mbError,
      MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorMessage: String;
begin
  if CurStep <> ssPostInstall then
    exit;

  if not InstallSystemAgentService(ErrorMessage) then
  begin
    CustomSetupExitCode := 21;
    RaiseException(ErrorMessage);
  end;

  if not RunShellIntegrationScript(
      'Register-DismodeShell.ps1',
      False,
      ErrorMessage) then
  begin
    CustomSetupExitCode := 20;
    RaiseException(ErrorMessage);
  end;

  { Nowa wersja juz dziala; pozostale pliki GameShift sa tylko balastem,
    wiec blad sprzatania nie cofa instalacji. }
  if (LegacyInstallPath <> '') and
     (not RunLegacyMigrationScript('Cleanup', ErrorMessage)) then
    Log(ErrorMessage);
end;

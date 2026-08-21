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
  #define AppVersion "0.3.0"
#endif

[Setup]
AppId={{8C8B533E-5B8B-46A7-B5F5-B3C2E5F7B9A1}
AppName=GameShift
AppVersion={#AppVersion}
AppVerName=GameShift {#AppVersion} Technical Preview
AppPublisher=GameShift Research Project
AppPublisherURL=https://gameshift-update-service-dev.dismonder.workers.dev/
AppSupportURL=https://gameshift-update-service-dev.dismonder.workers.dev/support
AppUpdatesURL=https://gameshift-update-service-dev.dismonder.workers.dev/
AppCopyright=Copyright (C) 2026 GameShift Research Project
AppComments=Lokalny, mierzalny i odwracalny optymalizator sesji gry dla Windows 11.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
Compression=lzma2/ultra64
DefaultDirName={autopf}\GameShift
DefaultGroupName=GameShift
DisableProgramGroupPage=yes
InfoBeforeFile={#RepoRoot}\installer\INSTALLER-NOTES.txt
LicenseFile={#RepoRoot}\installer\LICENSE-PL.txt
MinVersion=10.0.22621
OutputBaseFilename=GameShift-Setup-{#AppVersion}-win-x64
OutputDir={#OutputDir}
PrivilegesRequired=admin
RestartApplications=no
SetupIconFile={#RepoRoot}\assets\branding\GameShift.ico
SetupLogging=yes
SetupMutex=GameShift.Setup.8C8B533E5B8B46A7B5F5B3C2E5F7B9A1
SolidCompression=yes
UninstallDisplayIcon={app}\GameShift.exe
UninstallDisplayName=GameShift {#AppVersion} Technical Preview
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoCompany=GameShift Research Project
VersionInfoCopyright=Copyright (C) 2026 GameShift Research Project
VersionInfoDescription=Instalator GameShift Technical Preview
VersionInfoProductName=GameShift
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0
WizardStyle=modern

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "Utwórz skrót na pulpicie"; GroupDescription: "Dodatkowe skróty:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\installer\PRIVACY-PL.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

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
Name: "{group}\GameShift"; Filename: "{app}\GameShift.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GameShift"; Filename: "{app}\GameShift.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Classes\exefile\shell\GameShift"; ValueType: string; ValueName: "MUIVerb"; ValueData: "Uruchom przez GameShift"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\exefile\shell\GameShift"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\GameShift.exe,0"
Root: HKLM; Subkey: "Software\Classes\exefile\shell\GameShift"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKLM; Subkey: "Software\Classes\exefile\shell\GameShift\command"; ValueType: string; ValueName: ""; ValueData: """{app}\GameShift.exe"" --launch-through-gameshift ""%1"" --background"

[Run]
Filename: "{app}\GameShift.exe"; Description: "Uruchom GameShift"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
var
  CustomSetupExitCode: Integer;

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
     (WindowsVersion.Build >= 22000));
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

function PrepareInstalledGameShiftForMaintenance(
  var ErrorMessage: String): Boolean;
var
  HostPath: String;
  LauncherPath: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorMessage := '';
  HostPath := ExpandConstant('{app}\GameShift.SessionHost.exe');
  LauncherPath := ExpandConstant('{app}\GameShift.exe');

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
      '--prepare-update',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ErrorMessage :=
      'Nie udało się uruchomić bezpiecznej bramy aktualizacji GameShift.';
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
      'lub niedokończoną sesję. GameShift nie zamknął gry.'
  else if ResultCode = 4 then
    ErrorMessage :=
      'Journal recovery nie przeszedł kontroli integralności.'
  else
    ErrorMessage :=
      'Nie udało się zamknąć wyłącznie składników GameShift z katalogu ' +
      'instalacji. Gra i obce procesy nie zostały zakończone.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ShellError: String;
begin
  Result := '';
  if not PrepareInstalledGameShiftForMaintenance(Result) then
    exit;

  if not RunShellIntegrationScript(
      'Unregister-GameShiftShell.ps1',
      True,
      ShellError) then
    Result := ShellError;
end;

function InitializeUninstall: Boolean;
var
  ErrorMessage: String;
begin
  Result := PrepareInstalledGameShiftForMaintenance(ErrorMessage);
  if Result then
    Result := RunShellIntegrationScript(
      'Unregister-GameShiftShell.ps1',
      True,
      ErrorMessage);

  if not Result then
    MsgBox(
      ErrorMessage + #13#10 + #13#10 +
      'Dane recovery nie zostaną usunięte ani pominięte.',
      mbError,
      MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorMessage: String;
begin
  if CurStep <> ssPostInstall then
    exit;

  if not RunShellIntegrationScript(
      'Register-GameShiftShell.ps1',
      False,
      ErrorMessage) then
  begin
    CustomSetupExitCode := 20;
    RaiseException(ErrorMessage);
  end;
end;

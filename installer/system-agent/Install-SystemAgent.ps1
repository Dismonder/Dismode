[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serviceName = "DismodeSystemAgent"
$installationRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\.."))
$executablePath = [IO.Path]::GetFullPath(
    (Join-Path $installationRoot "Dismode.SystemAgent.exe"))
$serviceController = Join-Path $env:SystemRoot "System32\sc.exe"

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Brak pliku usługi: $executablePath"
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    & $serviceController create $serviceName `
        binPath= ('"' + $executablePath + '"') `
        start= delayed-auto `
        obj= LocalSystem `
        DisplayName= "Dismode System Optimizer Agent"
    if ($LASTEXITCODE -ne 0) {
        throw "Nie udało się utworzyć usługi $serviceName ($LASTEXITCODE)."
    }
}

& $serviceController config $serviceName `
    binPath= ('"' + $executablePath + '"') `
    start= delayed-auto `
    obj= LocalSystem `
    DisplayName= "Dismode System Optimizer Agent"
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się skonfigurować usługi $serviceName ($LASTEXITCODE)."
}

& $serviceController description $serviceName `
    "Pomiarowe optymalizacje Dismode, recovery i profile sesji gry."
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się ustawić opisu usługi $serviceName ($LASTEXITCODE)."
}

# Katalog danych maszyny: usluga LocalSystem trzyma w nim baze, dziennik
# recovery i liste zaufanych podpisow. ACL odziedziczony z ProgramData daje
# kazdemu uzytkownikowi prawo tworzenia plikow, wiec zwykly uzytkownik moglby
# podlozyc dziennik albo plik -wal bazy, ktory usluga wczyta jako wlasny, a
# katalog utworzony wczesniej przez interfejs bez uprawnien nalezalby do niego.
# Wlasciciel i DACL sa wiec ustawiane jawnie, a dzieci dziedzicza od nowa.
# Shared zostaje do zapisu dla uzytkownikow: tam interfejs bez uprawnien
# zapisuje stan gry dla Memory Optimizera.
$machineDataRoot = Join-Path (
    [Environment]::GetFolderPath("CommonApplicationData")) "Dismode"
$sharedDirectory = Join-Path $machineDataRoot "Shared"
$icacls = Join-Path $env:SystemRoot "System32\icacls.exe"
New-Item -ItemType Directory -Force -Path $machineDataRoot | Out-Null
New-Item -ItemType Directory -Force -Path $sharedDirectory | Out-Null
& $icacls $machineDataRoot /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się ustawić właściciela katalogu $machineDataRoot ($LASTEXITCODE)."
}

# /inheritance:r zdejmuje tylko wpisy odziedziczone; jawny wpis CREATOR OWNER
# nadany uzytkownikowi, ktory utworzyl katalog wczesniej, by przetrwal. Reset
# do samych wpisow odziedziczonych usuwa go, zanim dziedziczenie zostanie
# zdjete.
& $icacls $machineDataRoot /reset /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się zresetować uprawnień katalogu $machineDataRoot ($LASTEXITCODE)."
}

& $icacls $machineDataRoot /inheritance:r `
    /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" `
    /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się ustawić uprawnień katalogu $machineDataRoot ($LASTEXITCODE)."
}

& $icacls (Join-Path $machineDataRoot "*") /reset /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się odświeżyć uprawnień w katalogu $machineDataRoot ($LASTEXITCODE)."
}

& $icacls $sharedDirectory /grant "*S-1-5-32-545:(OI)(CI)M" /Q | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się ustawić uprawnień katalogu $sharedDirectory ($LASTEXITCODE)."
}

$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
    Start-Service -Name $serviceName
    $service.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(20))
}

$registered = Get-CimInstance Win32_Service -Filter (
    "Name='$serviceName'")
$registeredPath = $registered.PathName.Trim().Trim('"')
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        [IO.Path]::GetFullPath($registeredPath),
        $executablePath)) {
    throw "Usługa wskazuje nieoczekiwany plik: $($registered.PathName)"
}

$delayedAutoStart = Get-ItemPropertyValue -LiteralPath (
    "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName") `
    -Name DelayedAutoStart `
    -ErrorAction Stop
if ([int]$delayedAutoStart -ne 1) {
    throw "Usługa nie ma włączonego opóźnionego startu automatycznego."
}

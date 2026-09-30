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

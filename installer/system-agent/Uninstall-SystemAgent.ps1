[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$serviceName = "DismodeSystemAgent"
$serviceController = Join-Path $env:SystemRoot "System32\sc.exe"
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    exit 0
}

if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $serviceName -ErrorAction Stop
    $service.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(20))
}

& $serviceController delete $serviceName
if ($LASTEXITCODE -ne 0) {
    throw "Nie udało się usunąć usługi $serviceName ($LASTEXITCODE)."
}

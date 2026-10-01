[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = "Stop"
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$serviceExecutable = [IO.Path]::GetFullPath((Join-Path $installRoot (
    "Service\Dismode.MemoryService.exe")))
$trayExecutable = [IO.Path]::GetFullPath((Join-Path $installRoot (
    "Tray\Dismode.MemoryOptimizer.exe")))
foreach ($executable in @($serviceExecutable, $trayExecutable)) {
    if (-not $executable.StartsWith(
            $installRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
                [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Memory Optimizer executable is missing or outside its root: $executable"
    }
}

$serviceName = "DismodeMemoryService"
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$quotedServicePath = '"' + $serviceExecutable + '"'
if (Test-Path -LiteralPath $serviceKey) {
    & sc.exe config $serviceName `
        binPath= $quotedServicePath `
        start= delayed-auto `
        obj= LocalSystem `
        DisplayName= "Dismode Memory Optimizer Service"
}
else {
    & sc.exe create $serviceName `
        binPath= $quotedServicePath `
        start= delayed-auto `
        obj= LocalSystem `
        DisplayName= "Dismode Memory Optimizer Service"
}
if ($LASTEXITCODE -ne 0) {
    throw "Service registration failed with code $LASTEXITCODE."
}

& sc.exe description $serviceName (
    "Dismode GPL memory optimization service for Windows 11. " +
    "The service remains independent from gaming components.")
if ($LASTEXITCODE -ne 0) {
    throw "Service description failed with code $LASTEXITCODE."
}
& sc.exe failure $serviceName `
    reset= 86400 `
    actions= restart/5000/restart/15000/""/0
if ($LASTEXITCODE -ne 0) {
    throw "Service recovery configuration failed with code $LASTEXITCODE."
}

$taskService = New-Object -ComObject "Schedule.Service"
$taskService.Connect()
$taskFolder = $taskService.GetFolder("\")
$taskName = "Dismode Memory Optimizer"
$definition = $taskService.NewTask(0)
$definition.RegistrationInfo.Description =
    "Starts the unprivileged Dismode Memory Optimizer tray at user logon."
$definition.Settings.Enabled = $true
$definition.Settings.StartWhenAvailable = $true
$definition.Settings.DisallowStartIfOnBatteries = $false
$definition.Settings.StopIfGoingOnBatteries = $false
$definition.Settings.MultipleInstances = 0
$definition.Principal.GroupId = "S-1-5-4"
$definition.Principal.LogonType = 4
$definition.Principal.RunLevel = 0
$trigger = $definition.Triggers.Create(9)
$trigger.Enabled = $true
$action = $definition.Actions.Create(0)
$action.Path = $trayExecutable
$action.Arguments = "--startup"
$action.WorkingDirectory = Split-Path -Parent $trayExecutable
$null = $taskFolder.RegisterTaskDefinition(
    $taskName,
    $definition,
    6,
    $null,
    $null,
    4,
    $null)

& sc.exe start $serviceName
if ($LASTEXITCODE -notin @(0, 1056)) {
    throw "Service start failed with code $LASTEXITCODE."
}

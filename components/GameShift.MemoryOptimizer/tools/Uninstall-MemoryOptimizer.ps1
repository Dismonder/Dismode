[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$installRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$trayExecutable = [IO.Path]::GetFullPath((Join-Path $installRoot (
    "Tray\GameShift.MemoryOptimizer.exe")))
foreach ($process in Get-Process -Name "GameShift.MemoryOptimizer" `
        -ErrorAction SilentlyContinue) {
    $matchedInstall = $false
    try {
        $actualPath = [IO.Path]::GetFullPath($process.Path)
        if ([StringComparer]::OrdinalIgnoreCase.Equals(
                $actualPath,
                $trayExecutable)) {
            $matchedInstall = $true
            $process.Kill()
            if (-not $process.WaitForExit(5000)) {
                throw "Memory Optimizer tray did not exit within five seconds."
            }
        }
    }
    catch [System.ComponentModel.Win32Exception] {
        if ($matchedInstall) {
            throw
        }

        Write-Verbose (
            "Skipped an inaccessible Memory Optimizer process: " +
            $_.Exception.Message)
    }
    catch [System.InvalidOperationException] {
        Write-Verbose (
            "Skipped a Memory Optimizer process that already exited: " +
            $_.Exception.Message)
    }
    finally {
        $process.Dispose()
    }
}

$serviceName = "GameShiftMemoryService"
$serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
if (Test-Path -LiteralPath $serviceKey) {
    & sc.exe stop $serviceName | Out-Null
    if ($LASTEXITCODE -notin @(0, 1062)) {
        throw "Service stop failed with code $LASTEXITCODE."
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        if ($service.Status -eq
            [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            break
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($service.Status -ne
        [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        throw "Memory Optimizer service did not stop within 30 seconds."
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Service removal failed with code $LASTEXITCODE."
    }
}

$taskService = New-Object -ComObject "Schedule.Service"
$taskService.Connect()
$taskFolder = $taskService.GetFolder("\")
try {
    $taskFolder.DeleteTask("GameShift Memory Optimizer", 0)
}
catch [System.Runtime.InteropServices.COMException] {
    if ($_.Exception.HResult -ne -2147024894) {
        throw
    }
}

# Per-user settings and history under ProgramData are deliberately preserved.

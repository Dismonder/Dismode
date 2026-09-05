[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = "Stop"
$installRoot = [IO.Path]::GetFullPath($InstallDirectory)
$trayExecutable = [IO.Path]::GetFullPath((Join-Path $installRoot (
    "Tray\GameShift.MemoryOptimizer.exe")))
$installPrefix = $installRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $trayExecutable.StartsWith(
        $installPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Memory Optimizer tray path escaped the install directory."
}

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
if (-not (Test-Path -LiteralPath $serviceKey)) {
    return
}

& sc.exe stop $serviceName | Out-Null
if ($LASTEXITCODE -notin @(0, 1062)) {
    throw "Service stop failed with code $LASTEXITCODE."
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -eq
        [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        return
    }

    Start-Sleep -Milliseconds 250
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "Memory Optimizer service did not stop within 30 seconds."

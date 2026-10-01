[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Prepare", "Cleanup")]
    [string]$Phase,

    [Parameter(Mandatory = $true)]
    [string]$LegacyInstallDirectory,

    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

# Wydania do 0.7.0 nazywaly sie GameShift. Faza Prepare zdejmuje ich uslugi,
# rejestracje i skroty oraz przenosi dane maszyny, zanim nowe pliki trafia
# na dysk; wolno ja uruchomic dopiero po przejsciu bramy recovery starej
# wersji. Faza Cleanup usuwa stare pliki programu po udanej instalacji.

$ErrorActionPreference = "Stop"
$legacyName = "GameShift"
$legacyRoot = [IO.Path]::GetFullPath($LegacyInstallDirectory).TrimEnd('\')
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$powerShell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"

function Invoke-LegacyScript([string]$RelativePath) {
    $path = Join-Path $legacyRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return
    }

    & $powerShell -NoLogo -NoProfile -NonInteractive `
        -ExecutionPolicy Bypass -File $path
    if ($LASTEXITCODE -ne 0) {
        throw "Skrypt $RelativePath z instalacji $legacyName zakonczyl sie kodem $LASTEXITCODE."
    }
}

function Move-LegacyDirectory([string]$Source, [string]$Target) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        return
    }

    if (-not (Test-Path -LiteralPath $Target)) {
        Move-Item -LiteralPath $Source -Destination $Target
        return
    }

    # Nic nie jest nadpisywane: kolizje zostaja w starym katalogu.
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        $destination = Join-Path $Target $entry.Name
        if (-not (Test-Path -LiteralPath $destination)) {
            Move-Item -LiteralPath $entry.FullName -Destination $destination
        }
    }
}

if ($Phase -eq "Prepare") {
    Invoke-LegacyScript "SystemIntegration\system-agent\Uninstall-SystemAgent.ps1"
    Invoke-LegacyScript "MemoryOptimizer\tools\Uninstall-MemoryOptimizer.ps1"
    Invoke-LegacyScript "ShellIntegration\Unregister-GameShiftShell.ps1"

    foreach ($serviceName in @("GameShiftSystemAgent", "GameShiftMemoryService")) {
        if ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            throw "Usluga $serviceName nadal istnieje; usun ja poleceniem sc.exe delete $serviceName."
        }
    }

    $verbKey = "HKLM:\Software\Classes\exefile\shell\$legacyName"
    if (Test-Path -LiteralPath $verbKey) {
        Remove-Item -LiteralPath $verbKey -Recurse
    }

    $group = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) $legacyName
    if (Test-Path -LiteralPath $group -PathType Container) {
        Remove-Item -LiteralPath $group -Recurse
    }

    $desktopShortcut = Join-Path (
        [Environment]::GetFolderPath("CommonDesktopDirectory")) "$legacyName.lnk"
    if (Test-Path -LiteralPath $desktopShortcut -PathType Leaf) {
        Remove-Item -LiteralPath $desktopShortcut
    }

    $programData = [Environment]::GetFolderPath("CommonApplicationData")
    Move-LegacyDirectory `
        (Join-Path $programData $legacyName) `
        (Join-Path $programData "Dismode")
    exit 0
}

if (-not (Test-Path -LiteralPath $legacyRoot -PathType Container)) {
    exit 0
}

if ([StringComparer]::OrdinalIgnoreCase.Equals($legacyRoot, $installRoot)) {
    # Ten sam katalog: nowe pliki nazywaja sie Dismode*, wiec wszystko
    # z nazwa GameShift pochodzi ze starej wersji.
    Get-ChildItem -LiteralPath $legacyRoot -Recurse -File -Force `
        -Filter "*$legacyName*" |
        Remove-Item -Force
    exit 0
}

# Katalog usuwamy w calosci tylko wtedy, gdy byl domyslnym katalogiem
# GameShift; wtedy instalator przeniosl program obok, do katalogu Dismode.
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        (Split-Path -Leaf $legacyRoot),
        $legacyName)) {
    throw "Katalog $legacyRoot nie jest domyslnym katalogiem $legacyName; zostaje nietkniety."
}

Remove-Item -LiteralPath $legacyRoot -Recurse -Force

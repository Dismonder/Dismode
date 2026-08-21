[CmdletBinding()]
param(
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version = "0.1.9",
    [string]$InnoCompilerPath,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$payloadPath = Join-Path $artifactsRoot "GameShift-Installer-Payload"
$installerOutputPath = Join-Path $artifactsRoot "installer"
$installerScript = Join-Path $repositoryRoot "installer\GameShift.iss"
$releaseScript = Join-Path $PSScriptRoot "Build-LocalRelease.ps1"
$readmePath = Join-Path $repositoryRoot "README.md"

if (-not $InnoCompilerPath) {
    $innoCandidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    )
    $InnoCompilerPath = $innoCandidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

if (-not $InnoCompilerPath -or
    -not (Test-Path -LiteralPath $InnoCompilerPath)) {
    throw "Nie znaleziono ISCC.exe z Inno Setup 6. " +
        "Zainstaluj pakiet JRSoftware.InnoSetup albo podaj " +
        "-InnoCompilerPath."
}

foreach ($sourceFile in @(
        $installerScript,
        $releaseScript,
        $readmePath)) {
    if (-not (Test-Path -LiteralPath $sourceFile)) {
        throw "Brak wymaganego pliku: $sourceFile"
    }
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path (
    $installerOutputPath) -Force | Out-Null

$releaseArguments = @{
    OutputDirectory = $payloadPath
    SelfContained = $true
}
if ($SkipTests) {
    $releaseArguments.SkipTests = $true
}

& $releaseScript @releaseArguments
if ($LASTEXITCODE -ne 0) {
    throw "Budowa payloadu zakończyła się kodem $LASTEXITCODE."
}

Copy-Item -LiteralPath $readmePath -Destination (
    Join-Path $payloadPath "README.md") -Force

$manifestName = "GameShift-Payload.sha256"
$manifestPath = Join-Path $payloadPath $manifestName
$manifestLines = Get-ChildItem -LiteralPath $payloadPath -File -Recurse |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath(
            $payloadPath,
            $_.FullName).Replace("\", "/")
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relativePath"
    }
[IO.File]::WriteAllLines(
    $manifestPath,
    [string[]]$manifestLines,
    [Text.UTF8Encoding]::new($false))

$compilerArguments = @(
    "/DSourceDir=$payloadPath",
    "/DOutputDir=$installerOutputPath",
    "/DRepoRoot=$repositoryRoot",
    "/DAppVersion=$Version",
    $installerScript
)
& $InnoCompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup zakończył się kodem $LASTEXITCODE."
}

$setupPath = Join-Path $installerOutputPath (
    "GameShift-Setup-$Version-win-x64.exe")
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Kompilator nie utworzył oczekiwanego instalatora: $setupPath"
}

$setupFile = Get-Item -LiteralPath $setupPath
if ($setupFile.Length -lt 1MB) {
    throw "Instalator ma nieprawidłowy rozmiar: $($setupFile.Length) B."
}

$header = [byte[]]::new(2)
$stream = [IO.File]::OpenRead($setupPath)
try {
    if ($stream.Read($header, 0, 2) -ne 2 -or
        $header[0] -ne 0x4D -or
        $header[1] -ne 0x5A) {
        throw "Instalator nie ma poprawnego nagłówka Windows PE."
    }
}
finally {
    $stream.Dispose()
}

$setupHash = (
    Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
$hashPath = "$setupPath.sha256"
[IO.File]::WriteAllText(
    $hashPath,
    "$setupHash  $($setupFile.Name)`r`n",
    [Text.UTF8Encoding]::new($false))

$signature = Get-AuthenticodeSignature -FilePath $setupPath
Write-Output "Instalator gotowy: $setupPath"
Write-Output "Rozmiar: $([Math]::Round($setupFile.Length / 1MB, 2)) MiB"
Write-Output "SHA-256: $setupHash"
Write-Output "Podpis Authenticode: $($signature.Status)"
Write-Output "Manifest payloadu: $manifestPath"

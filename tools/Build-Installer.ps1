[CmdletBinding()]
param(
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version,
    [string]$InnoCompilerPath,
    [switch]$SkipTests,
    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$CodeSigningCertificateThumbprint =
        $env:DISMODE_RELEASE_SIGNING_THUMBPRINT,
    [string]$CodeSigningTimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowTestCodeSigningCertificate,
    [ValidateRange(1, 100)]
    [int]$BackupRetentionCount = 3
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$directoryBuildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
if (-not (Test-Path -LiteralPath $directoryBuildPropsPath)) {
    throw "Brak wymaganego pliku: $directoryBuildPropsPath"
}

[xml]$versionProperties = Get-Content -LiteralPath (
    $directoryBuildPropsPath) -Raw
$versionNode = $versionProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or
    $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props nie zawiera poprawnej wersji SemVer."
}
$repositoryVersion = $versionNode.InnerText
if ($Version -and -not [StringComparer]::Ordinal.Equals(
        $Version,
        $repositoryVersion)) {
    throw "Wersja instalatora $Version nie pasuje do wersji repozytorium " +
        "$repositoryVersion."
}
$Version = $repositoryVersion
if (-not $CodeSigningCertificateThumbprint) {
    throw "Wydanie $Version wymaga produkcyjnego certyfikatu. Ustaw " +
        "DISMODE_RELEASE_SIGNING_THUMBPRINT lub podaj " +
        "-CodeSigningCertificateThumbprint."
}

$artifactsRoot = Join-Path $repositoryRoot "artifacts"
$payloadPath = Join-Path $artifactsRoot "Dismode-App"
$installerOutputPath = Join-Path $artifactsRoot "installer"
$installerScript = Join-Path $repositoryRoot "installer\Dismode.iss"
$releaseScript = Join-Path $PSScriptRoot "Build-LocalRelease.ps1"
$updatePublisherProject = Join-Path $repositoryRoot (
    "tools\Dismode.UpdatePublisher\Dismode.UpdatePublisher.csproj")
$memoryOptimizerBuildScript = Join-Path $repositoryRoot (
    "components\Dismode.MemoryOptimizer\tools\Build-MemoryOptimizer.ps1")
$memoryOptimizerPayloadPath = Join-Path $artifactsRoot (
    "Dismode-MemoryOptimizer")
$updateAssetsPath = Join-Path $artifactsRoot "update-service-$Version"
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
        $directoryBuildPropsPath,
        $installerScript,
        $releaseScript,
        $updatePublisherProject,
        $memoryOptimizerBuildScript,
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
    CodeSigningCertificateThumbprint =
        $CodeSigningCertificateThumbprint
    CodeSigningTimestampUrl = $CodeSigningTimestampUrl
    AllowTestCodeSigningCertificate =
        $AllowTestCodeSigningCertificate
    BackupRetentionCount = $BackupRetentionCount
}
if ($SkipTests) {
    $releaseArguments.SkipTests = $true
}

& $releaseScript @releaseArguments
if ($LASTEXITCODE -ne 0) {
    throw "Budowa payloadu zakończyła się kodem $LASTEXITCODE."
}

$memoryOptimizerArguments = @{
    OutputDirectory = $memoryOptimizerPayloadPath
}
$memoryOptimizerArguments.CodeSigningCertificateThumbprint =
    $CodeSigningCertificateThumbprint
$memoryOptimizerArguments.CodeSigningTimestampUrl =
    $CodeSigningTimestampUrl
if ($SkipTests) {
    $memoryOptimizerArguments.SkipTests = $true
}
$memoryOptimizerArguments.AllowTestCodeSigningCertificate =
    $AllowTestCodeSigningCertificate
$memoryOptimizerArguments.BackupRetentionCount = $BackupRetentionCount
& $memoryOptimizerBuildScript @memoryOptimizerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Budowa Memory Optimizer zakończyła się kodem $LASTEXITCODE."
}

Copy-Item -LiteralPath $readmePath -Destination (
    Join-Path $payloadPath "README.md") -Force

$manifestName = "Dismode-Payload.sha256"
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
    "/DMemoryOptimizerDir=$memoryOptimizerPayloadPath",
    $installerScript
)
& $InnoCompilerPath @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup zakończył się kodem $LASTEXITCODE."
}

$setupPath = Join-Path $installerOutputPath (
    "Dismode-Setup-$Version-win-x64.exe")
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Kompilator nie utworzył oczekiwanego instalatora: $setupPath"
}

$windowsSdkBinRoot = Join-Path ${env:ProgramFiles(x86)} (
    "Windows Kits\10\bin")
$signToolPath = Get-ChildItem -LiteralPath $windowsSdkBinRoot `
        -Recurse `
        -Filter "signtool.exe" `
        -File |
    Where-Object {
        $_.FullName.EndsWith(
            "\x64\signtool.exe",
            [StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signToolPath) {
    throw "Nie znaleziono x64 SignTool.exe wymaganego do podpisania instalatora."
}

& $signToolPath `
    sign `
    /fd SHA256 `
    /td SHA256 `
    /tr $CodeSigningTimestampUrl `
    /s My `
    /sha1 $CodeSigningCertificateThumbprint `
    $setupPath
if ($LASTEXITCODE -ne 0) {
    throw "Podpisanie instalatora zakończyło się kodem $LASTEXITCODE."
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
if ($signature.Status -ne "Valid" -or
    $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne
        $CodeSigningCertificateThumbprint) {
    throw "Instalator nie ma oczekiwanego, ważnego podpisu Authenticode."
}
$publisherArguments = @(
    "run",
    "--project",
    $updatePublisherProject,
    "--configuration",
    "Release",
    "--no-build",
    "--no-restore",
    "--",
    "stage",
    "--installer",
    $setupPath,
    "--assets",
    $updateAssetsPath,
    "--version",
    $Version,
    "--channel",
    "preview",
    "--minimum-version",
    "0.3.0",
    "--note",
    "Dismode $Version Gaming Edition — lokalny staging preview.")
& dotnet @publisherArguments
if ($LASTEXITCODE -ne 0) {
    throw "Publikacja manifestu aktualizacji zakończyła się kodem $LASTEXITCODE."
}

Write-Output "Instalator gotowy: $setupPath"
Write-Output "Rozmiar: $([Math]::Round($setupFile.Length / 1MB, 2)) MiB"
Write-Output "SHA-256: $setupHash"
Write-Output "Podpis Authenticode: $($signature.Status)"
Write-Output "Manifest payloadu: $manifestPath"
Write-Output "Payload Memory Optimizer: $memoryOptimizerPayloadPath"
Write-Output "Manifest aktualizacji: $updateAssetsPath"

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PayloadDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$payloadPath = [IO.Path]::GetFullPath($PayloadDirectory)
if (-not (Test-Path -LiteralPath $payloadPath -PathType Container)) {
    throw "Memory Optimizer payload does not exist: $payloadPath"
}

$requiredFiles = @(
    "LICENSE",
    "NOTICE.md",
    "MODIFICATIONS.md",
    "README.md",
    "Release-Metadata.json",
    "tools\Prepare-MemoryOptimizerUpdate.ps1",
    "tools\Install-MemoryOptimizer.ps1",
    "tools\Uninstall-MemoryOptimizer.ps1",
    "Service\GameShift.MemoryService.exe",
    "Tray\GameShift.MemoryOptimizer.exe",
    "Source\GameShift.MemoryOptimizer-$Version-source.zip",
    "Source\GameShift.MemoryOptimizer-$Version-source.zip.sha256"
)
$missing = $requiredFiles |
    Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $payloadPath $_))
    }
if ($missing) {
    throw "GPL payload is incomplete: $($missing -join ', ')."
}

$debugSymbols = Get-ChildItem -LiteralPath $payloadPath `
    -Recurse `
    -File `
    -Filter "*.pdb"
if ($debugSymbols) {
    throw "GPL payload contains release debug symbols: " +
        "$($debugSymbols.FullName -join ', ')."
}

$sourceArchiveName = "GameShift.MemoryOptimizer-$Version-source.zip"
$noticeText = Get-Content -LiteralPath (
    Join-Path $payloadPath "NOTICE.md") -Raw
if (-not $noticeText.StartsWith(
        "# GameShift Memory Optimizer $Version ",
        [StringComparison]::Ordinal)) {
    throw "NOTICE.md does not identify release version $Version."
}
if ($noticeText.Contains("{{PRODUCT_VERSION}}", [StringComparison]::Ordinal)) {
    throw "NOTICE.md still contains an unexpanded product version token."
}
if (-not $noticeText.Contains(
        $sourceArchiveName,
        [StringComparison]::Ordinal)) {
    throw "NOTICE.md does not name the corresponding source archive $sourceArchiveName."
}
$sourceArchivePath = Join-Path $payloadPath "Source\$sourceArchiveName"
$sourceHashPath = "$sourceArchivePath.sha256"
$expectedHash = (Get-Content -LiteralPath $sourceHashPath -Raw).Trim().Split(
    [char[]]@(' ', "`t"),
    [StringSplitOptions]::RemoveEmptyEntries)[0]
$actualHash = (
    Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash
if (-not [StringComparer]::OrdinalIgnoreCase.Equals(
        $actualHash,
        $expectedHash)) {
    throw "Corresponding-source SHA-256 does not match."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($sourceArchivePath)
try {
    $entryNames = $archive.Entries.FullName
    foreach ($suffix in @(
            "/repository/Directory.Build.props",
            "/repository/components/GameShift.MemoryOptimizer/LICENSE",
            "/repository/components/GameShift.MemoryOptimizer/GameShift.MemoryOptimizer.sln",
            "/repository/components/GameShift.MemoryOptimizer/tools/Test-GplRelease.ps1",
            "/repository/components/GameShift.MemoryOptimizer/src/GameShift.MemoryOptimizer.Core/Native/WindowsMemoryOperations.cs")) {
        if (-not ($entryNames | Where-Object {
                    $_.EndsWith($suffix, [StringComparison]::Ordinal)
                })) {
            throw "Corresponding-source archive is missing $suffix."
        }
    }
}
finally {
    $archive.Dispose()
}

$forbiddenAssemblies = @(
    "GameShift.Core",
    "GameShift.Data",
    "GameShift.Windows",
    "GameShift.Contracts"
)
$dependencyFiles = Get-ChildItem -LiteralPath $payloadPath `
    -Recurse `
    -File `
    -Filter "*.deps.json"
foreach ($dependencyFile in $dependencyFiles) {
    $dependencyText = Get-Content -LiteralPath $dependencyFile.FullName -Raw
    foreach ($forbiddenAssembly in $forbiddenAssemblies) {
        if ($dependencyText.Contains(
                $forbiddenAssembly,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "GPL boundary violation in $($dependencyFile.FullName): " +
                "$forbiddenAssembly."
        }
    }
}

$metadata = Get-Content -LiteralPath (
    Join-Path $payloadPath "Release-Metadata.json") -Raw |
    ConvertFrom-Json
if ($metadata.version -ne $Version -or
    $metadata.license -ne "GPL-3.0-only" -or
    $metadata.correspondingSource.sha256 -ne $actualHash -or
    $metadata.upstream.version -ne "3.0.8") {
    throw "GPL release metadata is inconsistent."
}

Write-Output "GPL release gate passed: $payloadPath"

[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$SkipTests,
    [switch]$FrameworkDependent,
    [ValidatePattern("^[A-Fa-f0-9]{40}$")]
    [string]$CodeSigningCertificateThumbprint,
    [string]$CodeSigningTimestampUrl = "http://timestamp.digicert.com",
    [switch]$AllowTestCodeSigningCertificate,
    [ValidateRange(1, 100)]
    [int]$BackupRetentionCount = 3
)

$ErrorActionPreference = "Stop"
$componentRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $componentRoot "..\.."))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts"))
$versionPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
$solutionPath = Join-Path $componentRoot "GameShift.MemoryOptimizer.sln"
$releaseGatePath = Join-Path $PSScriptRoot "Test-GplRelease.ps1"

[xml]$versionProperties = Get-Content -LiteralPath $versionPropsPath -Raw
$versionNode = $versionProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or
    $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props does not contain a valid release version."
}
$version = $versionNode.InnerText

$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $artifactsRoot "GameShift-MemoryOptimizer"
}
elseif ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Memory Optimizer release must stay under $artifactsRoot."
}
$payloadStaging = "$outputPath.staging-$PID"
$sourceStaging = Join-Path $artifactsRoot (
    "GameShift-MemoryOptimizer-Source.staging-$PID")
foreach ($stagingPath in @($payloadStaging, $sourceStaging)) {
    if (Test-Path -LiteralPath $stagingPath) {
        throw "Staging directory already exists: $stagingPath"
    }
}

Push-Location $componentRoot
try {
    dotnet restore $solutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "Memory Optimizer restore failed with code $LASTEXITCODE."
    }

    dotnet build $solutionPath --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Memory Optimizer build failed with code $LASTEXITCODE."
    }

    if (-not $SkipTests) {
        dotnet test $solutionPath `
            --configuration Release `
            --no-build `
            --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Memory Optimizer tests failed with code $LASTEXITCODE."
        }

        dotnet format $solutionPath --verify-no-changes --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Memory Optimizer format verification failed."
        }
    }

    New-Item -ItemType Directory -Path $payloadStaging | Out-Null
    $projects = @(
        @{
            Project = Join-Path $componentRoot (
                "src\GameShift.MemoryService\GameShift.MemoryService.csproj")
            Destination = Join-Path $payloadStaging "Service"
            IsWinUi = $false
        },
        @{
            Project = Join-Path $componentRoot (
                "src\GameShift.MemoryOptimizer\GameShift.MemoryOptimizer.csproj")
            Destination = Join-Path $payloadStaging "Tray"
            IsWinUi = $true
        }
    )
    foreach ($project in $projects) {
        $arguments = @(
            "publish",
            $project.Project,
            "--configuration",
            "Release",
            "--output",
            $project.Destination,
            "--runtime",
            "win-x64"
        )
        if ($FrameworkDependent) {
            $arguments += @("--self-contained", "false")
        }
        else {
            $arguments += @("--self-contained", "true")
            if ($project.IsWinUi) {
                $arguments += "-p:WindowsAppSDKSelfContained=true"
            }
        }

        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Memory Optimizer publish failed: $($project.Project)"
        }
    }

    Get-ChildItem -LiteralPath $payloadStaging -File -Recurse -Filter "*.pdb" |
        Remove-Item -Force

    $trayExecutable = Join-Path $payloadStaging (
        "Tray\GameShift.MemoryOptimizer.exe")
    $startupProbe = Start-Process `
        -FilePath $trayExecutable `
        -ArgumentList "--startup-probe" `
        -WorkingDirectory (Split-Path -Parent $trayExecutable) `
        -WindowStyle Hidden `
        -PassThru
    try {
        if (-not $startupProbe.WaitForExit(10000)) {
            throw "Memory Optimizer tray startup probe timed out."
        }
        if ($startupProbe.ExitCode -ne 0) {
            throw "Memory Optimizer tray startup probe failed with code $($startupProbe.ExitCode)."
        }
    }
    finally {
        if (-not $startupProbe.HasExited) {
            Stop-Process -Id $startupProbe.Id -Force
            $startupProbe.WaitForExit()
        }
        $startupProbe.Dispose()
    }

    if ($CodeSigningCertificateThumbprint) {
        $codeSigningOid = "1.3.6.1.5.5.7.3.3"
        $signingCertificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object {
                $_.Thumbprint -eq $CodeSigningCertificateThumbprint.ToUpperInvariant() -and
                $_.HasPrivateKey -and
                $_.NotAfter -gt (Get-Date).AddDays(30) -and
                $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid
            } |
            Select-Object -First 1
        if (-not $signingCertificate) {
            throw "Nie znaleziono ważnego certyfikatu podpisywania kodu " +
                "$($CodeSigningCertificateThumbprint.ToUpperInvariant()) z kluczem prywatnym."
        }

        $isTestCodeSigningCertificate =
            [StringComparer]::OrdinalIgnoreCase.Equals(
                $signingCertificate.Subject,
                $signingCertificate.Issuer)
        if ($isTestCodeSigningCertificate -and
            -not $AllowTestCodeSigningCertificate) {
            throw "Wydanie Memory Optimizer wymaga produkcyjnego certyfikatu; " +
                "certyfikat jest samopodpisany."
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
            throw "Windows SDK x64 SignTool.exe was not found."
        }

        $ownedBinaries = @(
            (Join-Path $payloadStaging "Service\GameShift.MemoryService.exe"),
            (Join-Path $payloadStaging "Service\GameShift.MemoryService.dll"),
            (Join-Path $payloadStaging "Service\GameShift.MemoryOptimizer.Core.dll"),
            (Join-Path $payloadStaging "Tray\GameShift.MemoryOptimizer.exe"),
            (Join-Path $payloadStaging "Tray\GameShift.MemoryOptimizer.dll"),
            (Join-Path $payloadStaging "Tray\GameShift.MemoryOptimizer.Core.dll")
        )
        foreach ($binary in $ownedBinaries) {
            if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
                throw "Memory Optimizer binary to sign is missing: $binary"
            }

            & $signToolPath `
                sign `
                /fd SHA256 `
                /td SHA256 `
                /tr $CodeSigningTimestampUrl `
                /s My `
                /sha1 $CodeSigningCertificateThumbprint `
                $binary
            if ($LASTEXITCODE -ne 0) {
                throw "Memory Optimizer signing failed for $binary."
            }

            $binarySignature = Get-AuthenticodeSignature -LiteralPath $binary
            if ($binarySignature.Status -ne "Valid" -or
                $null -eq $binarySignature.SignerCertificate -or
                $binarySignature.SignerCertificate.Thumbprint -ne
                    $CodeSigningCertificateThumbprint) {
                throw "Memory Optimizer binary has an unexpected signature: $binary"
            }
        }
    }

    foreach ($document in @(
            "LICENSE",
            "NOTICE.md",
            "MODIFICATIONS.md",
            "README.md")) {
        Copy-Item -LiteralPath (
            Join-Path $componentRoot $document) -Destination $payloadStaging
    }
    $noticePath = Join-Path $payloadStaging "NOTICE.md"
    $noticePlaceholder = "{{PRODUCT_VERSION}}"
    $noticeText = Get-Content -LiteralPath $noticePath -Raw
    if (-not $noticeText.Contains(
            $noticePlaceholder,
            [StringComparison]::Ordinal)) {
        throw "Memory Optimizer NOTICE.md is missing the version placeholder."
    }
    [IO.File]::WriteAllText(
        $noticePath,
        $noticeText.Replace($noticePlaceholder, $version),
        [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (
        Join-Path $componentRoot "protocol") -Destination (
        Join-Path $payloadStaging "protocol") -Recurse
    $payloadToolsPath = Join-Path $payloadStaging "tools"
    New-Item -ItemType Directory -Path $payloadToolsPath | Out-Null
    foreach ($script in @(
            "Prepare-MemoryOptimizerUpdate.ps1",
            "Install-MemoryOptimizer.ps1",
            "Uninstall-MemoryOptimizer.ps1")) {
        Copy-Item -LiteralPath (
            Join-Path $PSScriptRoot $script) -Destination $payloadToolsPath
    }

    $sourceRootName = "GameShift.MemoryOptimizer-$version-source"
    $sourceRoot = Join-Path $sourceStaging $sourceRootName
    $sourceRepositoryRoot = Join-Path $sourceRoot "repository"
    $sourceComponentRoot = Join-Path $sourceRepositoryRoot (
        "components\GameShift.MemoryOptimizer")
    New-Item -ItemType Directory -Path $sourceComponentRoot | Out-Null
    $sourceFiles = Get-ChildItem -LiteralPath $componentRoot -File -Recurse |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            $_.Name -notlike '*.user'
        }
    foreach ($sourceFile in $sourceFiles) {
        $relativePath = [IO.Path]::GetRelativePath(
            $componentRoot,
            $sourceFile.FullName)
        $targetPath = Join-Path $sourceComponentRoot $relativePath
        $targetDirectory = Split-Path -Parent $targetPath
        if (-not (Test-Path -LiteralPath $targetDirectory)) {
            New-Item -ItemType Directory -Path $targetDirectory | Out-Null
        }
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetPath
    }
    Copy-Item -LiteralPath $versionPropsPath -Destination (
        Join-Path $sourceRepositoryRoot "Directory.Build.props")
    $globalJsonPath = Join-Path $repositoryRoot "global.json"
    if (Test-Path -LiteralPath $globalJsonPath) {
        Copy-Item -LiteralPath $globalJsonPath -Destination $sourceRepositoryRoot
    }

    $sourceOutputDirectory = Join-Path $payloadStaging "Source"
    New-Item -ItemType Directory -Path $sourceOutputDirectory | Out-Null
    $sourceArchiveName = "GameShift.MemoryOptimizer-$version-source.zip"
    $sourceArchivePath = Join-Path $sourceOutputDirectory $sourceArchiveName
    Compress-Archive -LiteralPath $sourceRoot -DestinationPath (
        $sourceArchivePath) -CompressionLevel Optimal
    $sourceHash = (
        Get-FileHash -LiteralPath $sourceArchivePath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        "$sourceArchivePath.sha256",
        "$sourceHash  $sourceArchiveName`n",
        [Text.UTF8Encoding]::new($false))

    $metadata = [ordered]@{
        version = $version
        license = "GPL-3.0-only"
        upstream = [ordered]@{
            name = "Windows Memory Cleaner"
            version = "3.0.8"
            author = "Igor Mundstein"
            url = "https://github.com/IgorMundstein/WinMemoryCleaner"
        }
        correspondingSource = [ordered]@{
            file = "Source/$sourceArchiveName"
            sha256 = $sourceHash
        }
        modifications = "MODIFICATIONS.md"
    }
    [IO.File]::WriteAllText(
        (Join-Path $payloadStaging "Release-Metadata.json"),
        ($metadata | ConvertTo-Json -Depth 5),
        [Text.UTF8Encoding]::new($false))

    $expectedFileVersion = "$version.0"
    foreach ($binary in @(
            (Join-Path $payloadStaging "Service\GameShift.MemoryService.exe"),
            (Join-Path $payloadStaging "Tray\GameShift.MemoryOptimizer.exe"),
            (Join-Path $payloadStaging "Service\GameShift.MemoryOptimizer.Core.dll"),
            (Join-Path $payloadStaging "Tray\GameShift.MemoryOptimizer.Core.dll"))) {
        if (-not (Test-Path -LiteralPath $binary)) {
            throw "Published binary is missing: $binary"
        }
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $binary).FileVersion
        if (-not $fileVersion.StartsWith(
                $expectedFileVersion,
                [StringComparison]::Ordinal)) {
            throw "Published binary version is inconsistent: $binary ($fileVersion)."
        }
    }

    & $releaseGatePath `
        -PayloadDirectory $payloadStaging `
        -Version $version
    if ($LASTEXITCODE -ne 0) {
        throw "GPL release gate failed with code $LASTEXITCODE."
    }

    $manifestPath = Join-Path $payloadStaging (
        "GameShift.MemoryOptimizer-Payload.sha256")
    $manifestLines = Get-ChildItem -LiteralPath $payloadStaging -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath(
                $payloadStaging,
                $_.FullName).Replace("\", "/")
            $hash = (
                Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$hash  $relativePath"
        }
    [IO.File]::WriteAllLines(
        $manifestPath,
        [string[]]$manifestLines,
        [Text.UTF8Encoding]::new($false))

    $backupPath = $null
    try {
        if (Test-Path -LiteralPath $outputPath) {
            $backupPath = "$outputPath.backup-$(
                Get-Date -Format 'yyyyMMdd-HHmmss')"
            if (Test-Path -LiteralPath $backupPath) {
                throw "Memory Optimizer backup already exists: $backupPath"
            }

            Move-Item -LiteralPath $outputPath -Destination $backupPath
        }

        Move-Item -LiteralPath $payloadStaging -Destination $outputPath
    }
    catch {
        if ($backupPath -and
            (Test-Path -LiteralPath $backupPath) -and
            -not (Test-Path -LiteralPath $outputPath)) {
            Move-Item -LiteralPath $backupPath -Destination $outputPath
        }

        throw
    }

    # Every build left a .backup-<timestamp> copy behind and nothing ever
    # removed them. Prune only after the swap succeeded, so an interrupted
    # build keeps its rollback point.
    $backupPattern = "^" +
        [regex]::Escape([IO.Path]::GetFileName($outputPath)) +
        "\.backup-\d{8}-\d{6}$"
    $staleBackups = Get-ChildItem -LiteralPath (
            Split-Path -Parent $outputPath) -Directory |
        Where-Object { $_.Name -match $backupPattern } |
        Sort-Object Name -Descending |
        Select-Object -Skip $BackupRetentionCount
    foreach ($staleBackup in $staleBackups) {
        try {
            Remove-Item -LiteralPath $staleBackup.FullName -Recurse -Force
            Write-Output "Removed stale backup: $($staleBackup.Name)"
        }
        catch {
            Write-Warning ("Could not remove backup " +
                "$($staleBackup.Name): $($_.Exception.Message)")
        }
    }

    Write-Output "Memory Optimizer release ready: $outputPath"
    if ($backupPath) {
        Write-Output "Previous Memory Optimizer release: $backupPath"
    }
}
finally {
    foreach ($stagingPath in @($payloadStaging, $sourceStaging)) {
        $resolvedStagingPath = [IO.Path]::GetFullPath($stagingPath)
        if ($resolvedStagingPath.StartsWith(
                $artifactsPrefix,
                [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $resolvedStagingPath)) {
            Remove-Item -LiteralPath $resolvedStagingPath -Recurse -Force
        }
    }
    Pop-Location
}

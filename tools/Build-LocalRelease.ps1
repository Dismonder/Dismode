[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts\GameShift-App",
    [switch]$CreateDesktopShortcut,
    [switch]$SelfContained,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts"))
$directoryBuildPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
$nativeShellProject = Join-Path $repositoryRoot (
    "src\GameShift.ShellExtension\GameShift.ShellExtension.vcxproj")
$nativeShellDll = Join-Path $repositoryRoot (
    "src\GameShift.ShellExtension\bin\x64\Release\" +
    "GameShift.ShellExtension.dll")
$sparseManifestSource = Join-Path $repositoryRoot (
    "installer\sparse-package\AppxManifest.xml")
$shellRegistrationScriptSource = Join-Path $repositoryRoot (
    "installer\sparse-package\Register-GameShiftShell.ps1")
$shellUnregistrationScriptSource = Join-Path $repositoryRoot (
    "installer\sparse-package\Unregister-GameShiftShell.ps1")
$outputPath = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot $OutputDirectory))
}

$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith(
        $artifactsPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Katalog wydania musi znajdować się wewnątrz $artifactsRoot."
}

$outputPrefix = $outputPath.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$runningComponents = Get-CimInstance Win32_Process |
    Where-Object {
        $_.ExecutablePath -and
        $_.ExecutablePath.StartsWith(
            $outputPrefix,
            [StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -in @(
            "GameShift.UI.exe",
            "GameShift.SessionHost.exe",
            "GameShift.SystemAgent.exe",
            "PresentMon-2.5.1-x64.exe")
    }
if ($runningComponents) {
    $description = $runningComponents |
        ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }
    throw "Wydanie jest uruchomione: $($description -join ', '). " +
        "Zakończ sesję i zamknij GameShift przed aktualizacją."
}

$journalPath = Join-Path (
    [Environment]::GetFolderPath("LocalApplicationData")) (
    "GameShift\user-recovery.jsonl")
if (Test-Path -LiteralPath $journalPath) {
    $checkpoints = Get-Content -LiteralPath $journalPath |
        ForEach-Object {
            $record = $_ | ConvertFrom-Json
            if ($null -ne $record.SessionCheckpoint) {
                $record
            }
        }
    $unfinishedSessions = $checkpoints |
        Group-Object SessionId |
        Where-Object {
            $latest = $_.Group |
                Sort-Object Sequence |
                Select-Object -Last 1
            [int]$latest.SessionCheckpoint -ne 10
        }
    if ($unfinishedSessions) {
        $sessionIds = $unfinishedSessions.Name -join ", "
        throw "Journal zawiera niedokończoną sesję: $sessionIds. " +
            "Uruchom bieżące wydanie i zakończ recovery przed aktualizacją."
    }
}

$stagingPath = "$outputPath.staging-$PID"
if (Test-Path -LiteralPath $stagingPath) {
    throw "Katalog staging już istnieje: $stagingPath"
}
$sparsePackageStagingPath = Join-Path $artifactsRoot (
    "GameShift-SparsePackage.staging-$PID")
if (Test-Path -LiteralPath $sparsePackageStagingPath) {
    throw "Katalog sparse package staging już istnieje: " +
        $sparsePackageStagingPath
}

foreach ($sourceFile in @(
        $directoryBuildPropsPath,
        $nativeShellProject,
        $sparseManifestSource,
        $shellRegistrationScriptSource,
        $shellUnregistrationScriptSource)) {
    if (-not (Test-Path -LiteralPath $sourceFile)) {
        throw "Brak wymaganego pliku wydania: $sourceFile"
    }
}

[xml]$versionProperties = Get-Content -LiteralPath (
    $directoryBuildPropsPath) -Raw
$versionNode = $versionProperties.SelectSingleNode(
    "/Project/PropertyGroup/Version")
if ($null -eq $versionNode -or
    $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props nie zawiera poprawnej wersji SemVer."
}
$applicationVersion = $versionNode.InnerText
$sparsePackageVersion = "$applicationVersion.0"

$vsWherePath = Join-Path ${env:ProgramFiles(x86)} (
    "Microsoft Visual Studio\Installer\vswhere.exe")
if (-not (Test-Path -LiteralPath $vsWherePath)) {
    throw "Nie znaleziono vswhere.exe wymaganego do budowy modułu powłoki."
}
$visualStudioPath = & $vsWherePath `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath |
    Select-Object -First 1
$nativeMsBuildPath = if ($visualStudioPath) {
    Join-Path $visualStudioPath "MSBuild\Current\Bin\MSBuild.exe"
}
else {
    $null
}
if (-not $nativeMsBuildPath -or
    -not (Test-Path -LiteralPath $nativeMsBuildPath)) {
    throw "Nie znaleziono MSBuild z narzędziami C++ x64."
}

$windowsSdkBinRoot = Join-Path ${env:ProgramFiles(x86)} (
    "Windows Kits\10\bin")
$makeAppxPath = Get-ChildItem -LiteralPath $windowsSdkBinRoot `
        -Recurse `
        -Filter "makeappx.exe" `
        -File |
    Where-Object {
        $_.FullName.EndsWith(
            "\x64\makeappx.exe",
            [StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $makeAppxPath) {
    throw "Nie znaleziono x64 MakeAppx.exe z Windows SDK."
}
$signToolPath = Join-Path (
    (Split-Path -Parent $makeAppxPath)) "signtool.exe"
if (-not (Test-Path -LiteralPath $signToolPath)) {
    throw "Nie znaleziono x64 SignTool.exe z Windows SDK."
}

$projects = @(
    "src\GameShift.Launcher\GameShift.Launcher.csproj",
    "src\GameShift.SessionHost\GameShift.SessionHost.csproj",
    "src\GameShift.SystemAgent\GameShift.SystemAgent.csproj",
    "src\GameShift.UI\GameShift.UI.csproj"
)
$presentMonSourceDirectory = Join-Path (
    $repositoryRoot) "third_party\PresentMon"
$presentMonExecutableName = "PresentMon-2.5.1-x64.exe"
$presentMonExpectedLength = 956768
$presentMonExpectedSha256 =
    "9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191"
$presentMonFiles = @(
    $presentMonExecutableName,
    "LICENSE.txt",
    "THIRD_PARTY.txt",
    "README.md"
)
$requiredFiles = @(
    "GameShift.exe",
    "GameShift.UI.exe",
    "GameShift.SessionHost.exe",
    "GameShift.SystemAgent.exe",
    "App.xbf",
    "MainWindow.xbf",
    "PerformanceOverlayWindow.xbf",
    "GameShift.UI.pri",
    "GameShift.ShellExtension.dll",
    "ShellIntegration\GameShift.Sparse.msix",
    "ShellIntegration\GameShift-Development.cer",
    "ShellIntegration\Register-GameShiftShell.ps1",
    "ShellIntegration\Unregister-GameShiftShell.ps1",
    "Tools\PresentMon\$presentMonExecutableName",
    "Tools\PresentMon\LICENSE.txt",
    "Tools\PresentMon\THIRD_PARTY.txt",
    "Tools\PresentMon\README.md"
)
if ($SelfContained) {
    $requiredFiles += @(
        "coreclr.dll",
        "hostfxr.dll",
        "Microsoft.WindowsAppRuntime.dll"
    )
}

Push-Location $repositoryRoot
try {
    $missingPresentMonFiles = $presentMonFiles |
        Where-Object {
            -not (Test-Path -LiteralPath (
                Join-Path $presentMonSourceDirectory $_))
        }
    if ($missingPresentMonFiles) {
        throw "Brak plików PresentMon: " +
            "$($missingPresentMonFiles -join ', ')."
    }

    $presentMonSourceExecutable = Join-Path (
        $presentMonSourceDirectory) $presentMonExecutableName
    $presentMonFile = Get-Item -LiteralPath $presentMonSourceExecutable
    $presentMonActualSha256 = (
        Get-FileHash -LiteralPath $presentMonSourceExecutable -Algorithm SHA256
    ).Hash
    if ($presentMonFile.Length -ne $presentMonExpectedLength -or
        -not [StringComparer]::Ordinal.Equals(
            $presentMonActualSha256,
            $presentMonExpectedSha256)) {
        throw "PresentMon nie przeszedł weryfikacji rozmiaru i SHA-256."
    }

    dotnet restore GameShift.sln
    if ($LASTEXITCODE -ne 0) {
        throw "Restore zakończył się kodem $LASTEXITCODE."
    }

    dotnet build GameShift.sln --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Build zakończył się kodem $LASTEXITCODE."
    }

    $journalVerifier = (
        "tools\GameShift.SessionSimulator\bin\Release\" +
        "net10.0-windows10.0.26100.0\" +
        "GameShift.SessionSimulator.dll")
    dotnet $journalVerifier --journal-status
    if ($LASTEXITCODE -ne 0) {
        throw "Integralny journal nie potwierdził zakończenia " +
            "wszystkich sesji (kod $LASTEXITCODE)."
    }

    if (-not $SkipTests) {
        dotnet test GameShift.sln `
            --configuration Release `
            --no-build `
            --no-restore `
            -m:1
        if ($LASTEXITCODE -ne 0) {
            throw "Testy zakończyły się kodem $LASTEXITCODE."
        }

        dotnet format GameShift.sln `
            --verify-no-changes `
            --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Format verification zakończył się kodem $LASTEXITCODE."
        }
    }

    & $nativeMsBuildPath `
        $nativeShellProject `
        "/m" `
        "/p:Configuration=Release" `
        "/p:Platform=x64" `
        "/v:minimal"
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $nativeShellDll)) {
        throw "Budowa modułu powłoki Windows 11 nie powiodła się."
    }

    if ($SelfContained) {
        foreach ($project in $projects) {
            $restoreArguments = @(
                "restore",
                $project,
                "--runtime",
                "win-x64"
            )
            if ($project.EndsWith(
                    "GameShift.UI.csproj",
                    [StringComparison]::OrdinalIgnoreCase)) {
                $restoreArguments +=
                    "-p:WindowsAppSDKSelfContained=true"
            }

            & dotnet @restoreArguments
            if ($LASTEXITCODE -ne 0) {
                throw "Restore win-x64 nie powiódł się: $project"
            }
        }
    }

    New-Item -ItemType Directory -Path $stagingPath | Out-Null
    foreach ($project in $projects) {
        $publishArguments = @(
            "publish",
            $project,
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            $stagingPath
        )
        if ($SelfContained) {
            $publishArguments += @(
                "--runtime",
                "win-x64",
                "--self-contained",
                "true"
            )
            if ($project.EndsWith(
                    "GameShift.UI.csproj",
                    [StringComparison]::OrdinalIgnoreCase)) {
                $publishArguments +=
                    "-p:WindowsAppSDKSelfContained=true"
            }
        }

        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Publikacja nie powiodła się: $project"
        }
    }

    $presentMonTargetDirectory = Join-Path (
        $stagingPath) "Tools\PresentMon"
    New-Item -ItemType Directory -Path (
        $presentMonTargetDirectory) | Out-Null
    foreach ($presentMonFileName in $presentMonFiles) {
        Copy-Item -LiteralPath (
            Join-Path $presentMonSourceDirectory $presentMonFileName
        ) -Destination $presentMonTargetDirectory
    }

    Copy-Item -LiteralPath $nativeShellDll -Destination (
        Join-Path $stagingPath "GameShift.ShellExtension.dll")

    Get-ChildItem -LiteralPath $stagingPath `
        -Recurse `
        -File `
        -Filter "*.pdb" |
        Remove-Item -Force
    $remainingSymbols = Get-ChildItem -LiteralPath $stagingPath `
        -Recurse `
        -File `
        -Filter "*.pdb"
    if ($remainingSymbols) {
        throw "Publiczny payload nadal zawiera symbole PDB."
    }

    New-Item -ItemType Directory -Path (
        $sparsePackageStagingPath) | Out-Null
    $sparseManifestText = Get-Content -LiteralPath (
        $sparseManifestSource) -Raw
    $sparseManifestText = $sparseManifestText.Replace(
        'Version="0.0.0.0"',
        "Version=`"$sparsePackageVersion`"")
    if ($sparseManifestText -notmatch (
            'Version="' + [regex]::Escape($sparsePackageVersion) + '"')) {
        throw "Nie udało się ustawić wersji sparse package."
    }
    [IO.File]::WriteAllText(
        (Join-Path $sparsePackageStagingPath "AppxManifest.xml"),
        $sparseManifestText,
        [Text.UTF8Encoding]::new($false))

    $shellIntegrationDirectory = Join-Path (
        $stagingPath) "ShellIntegration"
    New-Item -ItemType Directory -Path (
        $shellIntegrationDirectory) | Out-Null
    $sparsePackagePath = Join-Path (
        $shellIntegrationDirectory) "GameShift.Sparse.msix"
    & $makeAppxPath `
        pack `
        /o `
        /d $sparsePackageStagingPath `
        /nv `
        /p $sparsePackagePath
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $sparsePackagePath)) {
        throw "Budowa sparse package dla menu Windows 11 nie powiodła się."
    }

    $certificateSubject = "CN=GameShift Development"
    $codeSigningOid = "1.3.6.1.5.5.7.3.3"
    $signingCertificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $certificateSubject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            $_.EnhancedKeyUsageList.Value -contains $codeSigningOid
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $signingCertificate) {
        $signingCertificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $certificateSubject `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(5)
    }
    if (-not $signingCertificate -or
        $signingCertificate.Subject -ne $certificateSubject -or
        -not $signingCertificate.HasPrivateKey) {
        throw "Brak poprawnego certyfikatu deweloperskiego GameShift."
    }

    $currentUserTrustStore = [Security.Cryptography.X509Certificates.X509Store]::new(
        "TrustedPeople",
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $currentUserTrustStore.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $alreadyTrusted = $currentUserTrustStore.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $signingCertificate.Thumbprint,
            $false).Count -gt 0
        if (-not $alreadyTrusted) {
            $currentUserTrustStore.Add($signingCertificate)
        }
    }
    finally {
        $currentUserTrustStore.Dispose()
    }

    & $signToolPath `
        sign `
        /fd SHA256 `
        /s My `
        /sha1 $signingCertificate.Thumbprint `
        $sparsePackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "Podpisanie sparse package nie powiodło się."
    }
    $packageSignature = Get-AuthenticodeSignature -LiteralPath (
        $sparsePackagePath)
    if ($null -eq $packageSignature.SignerCertificate -or
        $packageSignature.SignerCertificate.Thumbprint -ne
            $signingCertificate.Thumbprint -or
        $packageSignature.Status -in @("NotSigned", "HashMismatch")) {
        throw "Weryfikacja integralności podpisu sparse package " +
            "nie powiodła się."
    }

    $developmentCertificatePath = Join-Path (
        $shellIntegrationDirectory) "GameShift-Development.cer"
    [IO.File]::WriteAllBytes(
        $developmentCertificatePath,
        $signingCertificate.Export(
            [Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Copy-Item -LiteralPath (
        $shellRegistrationScriptSource) -Destination (
        $shellIntegrationDirectory)
    Copy-Item -LiteralPath (
        $shellUnregistrationScriptSource) -Destination (
        $shellIntegrationDirectory)
    Remove-Item -LiteralPath $sparsePackageStagingPath `
        -Recurse `
        -Force

    $missingFiles = $requiredFiles |
        Where-Object {
            -not (Test-Path -LiteralPath (
                Join-Path $stagingPath $_))
        }
    if ($missingFiles) {
        throw "Brak wymaganych plików: $($missingFiles -join ', ')."
    }

    $backupPath = $null
    try {
        if (Test-Path -LiteralPath $outputPath) {
            $backupPath = "$outputPath.backup-$(
                Get-Date -Format 'yyyyMMdd-HHmmss')"
            if (Test-Path -LiteralPath $backupPath) {
                throw "Katalog kopii już istnieje: $backupPath"
            }

            Move-Item -LiteralPath $outputPath -Destination $backupPath
        }

        Move-Item -LiteralPath $stagingPath -Destination $outputPath
    }
    catch {
        if ($backupPath -and
            (Test-Path -LiteralPath $backupPath) -and
            -not (Test-Path -LiteralPath $outputPath)) {
            Move-Item -LiteralPath $backupPath -Destination $outputPath
        }

        throw
    }

    if ($CreateDesktopShortcut) {
        $desktop = [Environment]::GetFolderPath("Desktop")
        $shortcutPath = Join-Path $desktop "GameShift.lnk"
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $outputPath "GameShift.exe"
        $shortcut.WorkingDirectory = $outputPath
        $shortcut.Description = "Uruchom GameShift"
        $shortcut.Save()
    }

    Write-Output "Wydanie gotowe: $outputPath"
    if ($backupPath) {
        Write-Output "Kopia poprzedniej wersji: $backupPath"
    }
}
finally {
    if (Test-Path -LiteralPath $sparsePackageStagingPath) {
        Remove-Item -LiteralPath $sparsePackageStagingPath `
            -Recurse `
            -Force
    }
    Pop-Location
}

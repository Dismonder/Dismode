[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [ValidateRange(1, 100)]
    [int]$BackupRetentionCount = 3,
    [ValidateRange(1, 100)]
    [int]$InstallerRetentionCount = 3,
    [ValidateRange(1, 100)]
    [int]$UpdateAssetRetentionCount = 3,
    [switch]$IncludeCertificates,
    [switch]$Apply
)

$ErrorActionPreference = "Stop"

# Sprzątanie po budowaniu: stare kopie wydania, stare instalatory, stare
# paczki update-service i porzucone certyfikaty deweloperskie. Bez -Apply
# skrypt tylko pokazuje plan i niczego nie rusza — kasowanie katalogów
# i certyfikatów jest nieodwracalne.

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot "artifacts"))
if (-not (Test-Path -LiteralPath $artifactsRoot)) {
    throw "Brak katalogu artefaktów: $artifactsRoot"
}
$artifactsPrefix = $artifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

$certificateSubject = "CN=GameShift Development"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"

function Get-PathSize {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-Item -LiteralPath $Path).Length
    }

    $total = 0L
    Get-ChildItem -LiteralPath $Path -Recurse -File -Force `
            -ErrorAction SilentlyContinue |
        ForEach-Object { $total += $_.Length }
    return $total
}

function Get-VersionKey {
    param([string]$Text)

    $match = [regex]::Match($Text, '\d+\.\d+\.\d+')
    if ($match.Success) {
        return [version]$match.Value
    }

    return [version]"0.0.0"
}

function New-PlanItem {
    param(
        [string]$Category,
        [string]$Name,
        [string]$Path,
        [string]$Kind,
        [long]$Bytes
    )

    return [pscustomobject]@{
        Category = $Category
        Name = $Name
        Path = $Path
        Kind = $Kind
        Bytes = $Bytes
    }
}

$plan = [Collections.Generic.List[object]]::new()
$kept = [Collections.Generic.List[string]]::new()

# --- kopie poprzednich wydań -------------------------------------------
foreach ($prefix in @("GameShift-App", "GameShift-MemoryOptimizer")) {
    $pattern = "^" + [regex]::Escape($prefix) + "\.backup-\d{8}-\d{6}$"
    $backups = Get-ChildItem -LiteralPath $artifactsRoot -Directory |
        Where-Object { $_.Name -match $pattern } |
        Sort-Object Name -Descending
    $backups |
        Select-Object -First $BackupRetentionCount |
        ForEach-Object { $kept.Add("kopia: $($_.Name)") }
    foreach ($backup in ($backups | Select-Object -Skip $BackupRetentionCount)) {
        $plan.Add((New-PlanItem `
            -Category "kopie wydania" `
            -Name $backup.Name `
            -Path $backup.FullName `
            -Kind "Katalog" `
            -Bytes (Get-PathSize $backup.FullName)))
    }
}

# --- zbudowane instalatory ---------------------------------------------
$installerRoot = Join-Path $artifactsRoot "installer"
if (Test-Path -LiteralPath $installerRoot) {
    $installers = Get-ChildItem -LiteralPath $installerRoot -File `
            -Filter "GameShift-Setup-*.exe" |
        Sort-Object @{ Expression = { Get-VersionKey $_.Name } } -Descending
    $installers |
        Select-Object -First $InstallerRetentionCount |
        ForEach-Object { $kept.Add("instalator: $($_.Name)") }
    $staleInstallers = $installers |
        Select-Object -Skip $InstallerRetentionCount
    foreach ($installer in $staleInstallers) {
        $plan.Add((New-PlanItem `
            -Category "instalatory" `
            -Name $installer.Name `
            -Path $installer.FullName `
            -Kind "Plik" `
            -Bytes $installer.Length))
        # Suma kontrolna bez instalatora jest bezużyteczna, więc idzie razem.
        $hashPath = "$($installer.FullName).sha256"
        if (Test-Path -LiteralPath $hashPath -PathType Leaf) {
            $plan.Add((New-PlanItem `
                -Category "instalatory" `
                -Name "$($installer.Name).sha256" `
                -Path $hashPath `
                -Kind "Plik" `
                -Bytes (Get-PathSize $hashPath)))
        }
    }
}

# --- staging kanału aktualizacji ---------------------------------------
$updateAssets = Get-ChildItem -LiteralPath $artifactsRoot -Directory |
    Where-Object { $_.Name -match '^update-service-\d+\.\d+\.\d+$' } |
    Sort-Object @{ Expression = { Get-VersionKey $_.Name } } -Descending
$updateAssets |
    Select-Object -First $UpdateAssetRetentionCount |
    ForEach-Object { $kept.Add("paczka aktualizacji: $($_.Name)") }
$staleUpdateAssets = $updateAssets |
    Select-Object -Skip $UpdateAssetRetentionCount
foreach ($asset in $staleUpdateAssets) {
    $plan.Add((New-PlanItem `
        -Category "paczki aktualizacji" `
        -Name $asset.Name `
        -Path $asset.FullName `
        -Kind "Katalog" `
        -Bytes (Get-PathSize $asset.FullName)))
}

# --- certyfikaty deweloperskie -----------------------------------------
if ($IncludeCertificates) {
    # Chronimy każdy odcisk, który dziś do czegoś służy: zaufany przez
    # Windows dla zarejestrowanej paczki sparse, dołączony do bieżącego
    # payloadu albo do instalacji, oraz ten, który wybierze kolejne
    # budowanie. Magazynów LocalMachine skrypt nie dotyka w ogóle — leży
    # tam kotwica zaufania dla zainstalowanego menu kontekstowego.
    $protectedThumbprints = [Collections.Generic.List[string]]::new()
    Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $certificateSubject } |
        ForEach-Object { $protectedThumbprints.Add($_.Thumbprint) }
    foreach ($certificateFile in @(
            (Join-Path $artifactsRoot (
                "GameShift-App\ShellIntegration\GameShift-Development.cer")),
            (Join-Path $env:ProgramFiles (
                "GameShift\ShellIntegration\GameShift-Development.cer")))) {
        if (Test-Path -LiteralPath $certificateFile -PathType Leaf) {
            $protectedThumbprints.Add(
                ([Security.Cryptography.X509Certificates.X509Certificate2]::new(
                    $certificateFile)).Thumbprint)
        }
    }
    $nextBuildCertificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $_.Subject -eq $certificateSubject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid
        } |
        Sort-Object NotAfter, Thumbprint -Descending |
        Select-Object -First 1
    if ($nextBuildCertificate) {
        $protectedThumbprints.Add($nextBuildCertificate.Thumbprint)
    }

    $protectedThumbprints = @($protectedThumbprints |
        Where-Object { $_ } |
        Sort-Object -Unique)
    foreach ($thumbprint in $protectedThumbprints) {
        $kept.Add("certyfikat: $thumbprint")
    }

    foreach ($storeName in @("My", "TrustedPeople", "CA")) {
        Get-ChildItem "Cert:\CurrentUser\$storeName" `
                -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Subject -eq $certificateSubject -and
                $protectedThumbprints -notcontains $_.Thumbprint
            } |
            ForEach-Object {
                $plan.Add((New-PlanItem `
                    -Category "certyfikaty" `
                    -Name "CurrentUser\$storeName $($_.Thumbprint)" `
                    -Path "Cert:\CurrentUser\$storeName\$($_.Thumbprint)" `
                    -Kind "Certyfikat" `
                    -Bytes 0))
            }
    }
}

# --- raport -------------------------------------------------------------
Write-Output "Zachowane:"
foreach ($entry in $kept) {
    Write-Output "  $entry"
}

Write-Output ""
Write-Output "Do usunięcia:"
if ($plan.Count -eq 0) {
    Write-Output "  nic — nie ma czego sprzątać."
    return
}

foreach ($group in ($plan | Group-Object Category)) {
    $groupBytes = ($group.Group | Measure-Object Bytes -Sum).Sum
    Write-Output ("  {0} — {1} poz., {2:N2} GB" -f `
        $group.Name, $group.Count, ($groupBytes / 1GB))
    foreach ($item in $group.Group) {
        Write-Output ("      {0}" -f $item.Name)
    }
}
$totalBytes = ($plan | Measure-Object Bytes -Sum).Sum
Write-Output ""
Write-Output ("Razem: {0} pozycji, {1:N2} GB" -f `
    $plan.Count, ($totalBytes / 1GB))

if (-not $Apply) {
    Write-Output ""
    Write-Output "To był tylko podgląd. Aby usunąć, uruchom ponownie z -Apply."
    if (-not $IncludeCertificates) {
        Write-Output "Certyfikaty obejmuje dopiero -IncludeCertificates."
    }

    return
}

# --- kasowanie ----------------------------------------------------------
$description = "{0} pozycji, {1:N2} GB" -f $plan.Count, ($totalBytes / 1GB)
if (-not $PSCmdlet.ShouldProcess($description, "Usunąć bezpowrotnie")) {
    Write-Output "Przerwane — nic nie usunięto."
    return
}

$removedBytes = 0L
$failures = 0
foreach ($item in $plan) {
    try {
        if ($item.Kind -eq "Certyfikat") {
            if ($item.Path -like "Cert:\CurrentUser\My\*") {
                Remove-Item -LiteralPath $item.Path -DeleteKey -Force `
                    -Confirm:$false
            }
            else {
                Remove-Item -LiteralPath $item.Path -Force -Confirm:$false
            }
        }
        else {
            # Po katalogu wydania nie powinno zostać nic poza artifacts;
            # gdyby plan zawierał cokolwiek spoza, przerywamy.
            if (-not $item.Path.StartsWith(
                    $artifactsPrefix,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw "Ścieżka spoza artifacts: $($item.Path)"
            }

            Remove-Item -LiteralPath $item.Path -Recurse -Force `
                -Confirm:$false
        }

        $removedBytes += $item.Bytes
    }
    catch {
        $failures++
        Write-Warning "Nie udało się usunąć $($item.Name): $($_.Exception.Message)"
    }
}

Write-Output ("Usunięto {0} z {1} pozycji, zwolnione {2:N2} GB." -f `
    ($plan.Count - $failures), $plan.Count, ($removedBytes / 1GB))
if ($failures -gt 0) {
    throw "Nie udało się usunąć $failures pozycji."
}

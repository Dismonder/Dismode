#Requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$packageName = "Dismode.Desktop"
$externalLocation = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$packagePath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "Dismode.Sparse.msix"))
$certificatePath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "Dismode-Development.cer"))
$launcherPath = Join-Path $externalLocation "Dismode.exe"
$extensionPath = Join-Path $externalLocation "Dismode.ShellExtension.dll"

foreach ($requiredFile in @(
        $packagePath,
        $certificatePath,
        $launcherPath,
        $extensionPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Brak wymaganego pliku integracji powłoki: $requiredFile"
    }
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath)
$store = [Security.Cryptography.X509Certificates.X509Store]::new(
    "TrustedPeople",
    [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
$certificateWasAdded = $false
try {
    $store.Open(
        [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $alreadyTrusted = $store.Certificates.Find(
        [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
        $certificate.Thumbprint,
        $false).Count -gt 0
    if (-not $alreadyTrusted) {
        $store.Add($certificate)
        $certificateWasAdded = $true
    }
}
finally {
    $store.Dispose()
}

try {
    Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-AppxPackage `
                -Package $_.PackageFullName `
                -ErrorAction Stop
        }

    Add-AppxPackage `
        -Path $packagePath `
        -ExternalLocation $externalLocation `
        -ErrorAction Stop

    $registered = Get-AppxPackage `
        -Name $packageName `
        -ErrorAction SilentlyContinue
    if ($null -eq $registered) {
        throw "Windows nie potwierdził rejestracji menu Dismode."
    }
}
catch {
    if ($certificateWasAdded) {
        $cleanupStore = [Security.Cryptography.X509Certificates.X509Store]::new(
            "TrustedPeople",
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
        try {
            $cleanupStore.Open(
                [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $cleanupStore.Remove($certificate)
        }
        finally {
            $cleanupStore.Dispose()
        }
    }

    throw
}

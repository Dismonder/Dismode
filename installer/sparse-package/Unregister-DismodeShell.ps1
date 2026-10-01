#Requires -Version 5.1

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$packageName = "Dismode.Desktop"
$certificatePath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "Dismode-Development.cer"))
Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue |
    ForEach-Object {
        Remove-AppxPackage `
            -Package $_.PackageFullName `
            -ErrorAction Stop
    }

$remaining = Get-AppxPackage `
    -Name $packageName `
    -ErrorAction SilentlyContinue
if ($null -ne $remaining) {
    throw "Windows nie usunął rejestracji menu Dismode."
}

if (Test-Path -LiteralPath $certificatePath -PathType Leaf) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificatePath)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        "TrustedPeople",
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $certificate.Thumbprint,
            $false)
        foreach ($match in $matches) {
            $store.Remove($match)
        }
    }
    finally {
        $store.Dispose()
    }
}

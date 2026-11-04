param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Package
)
$ErrorActionPreference = 'Stop'
$packagedCore = Join-Path $Package 'core/ior-core/ior-core.exe'
$adapter = Join-Path $Package 'core/ior-core/ior-awg-adapter.exe'
foreach ($file in @($Source, $packagedCore, $adapter)) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required native AWG component missing: $file"
    }
}
$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
$packageHash = (Get-FileHash -LiteralPath $packagedCore -Algorithm SHA256).Hash
if ($sourceHash -ne $packageHash) {
    throw 'Packaged ior-core differs from the newly built binary.'
}
Write-Host "Native AWG core verified: $($packageHash.Substring(0, 12).ToLowerInvariant())"
